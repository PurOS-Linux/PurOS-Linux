#!/usr/bin/env bash
# Standalone ISO builder: works on any Arch Linux machine or WSL2 Arch container without private /root files.
#   usage: build.sh [/path/to/FurrOS-clean]
set -euo pipefail
SRC=${1:-/var/tmp/furros-src}
WORK=${WORK:-/var/tmp/furros}
OUT_DIR=${OUT_DIR:-$WORK/out}
ISO_NAME=${CUSTOM_ISO_NAME:-puros}
BUILD_FURRBOOT=${BUILD_FURRBOOT:-1}
BUILD_AUR_WALLS=${BUILD_AUR_WALLS:-1}
CUSTOM_VIDEOS_DIR=${CUSTOM_VIDEOS_DIR:-$SRC/custom_wallpapers}
export GNUPGHOME=${GNUPGHOME:-/var/tmp/furros-gpg}
PROFILE=$WORK/profile
AIR=$PROFILE/airootfs

log() { printf '\e[38;2;127;240;240m🐾 %s\e[0m\n' "$*"; }

log "Checking builder dependencies (archiso, squashfs-tools, mtools, xorriso, base-devel)..."
pacman-key --init >/dev/null 2>&1 || true
pacman-key --populate archlinux >/dev/null 2>&1 || true
pacman -Sy --needed --noconfirm archlinux-keyring >/dev/null 2>&1 || true
pacman -S --needed --noconfirm archiso squashfs-tools dosfstools mtools libisoburn git base-devel python gnupg curl sudo gawk sed grep

id -u builder >/dev/null 2>&1 || useradd -m -s /bin/bash builder
printf 'builder ALL=(ALL) NOPASSWD: /usr/bin/pacman\n' > /etc/sudoers.d/99-furros-builder
chmod 440 /etc/sudoers.d/99-furros-builder

# a mirror that stopped syncing quietly bakes weeks-old packages (and missing security fixes) into the ISO:
# drop every mirror more than 3 days behind before building (backup in mirrorlist.furros-bak)
log "Mirror freshness"
now=$(date +%s)
cp /etc/pacman.d/mirrorlist /etc/pacman.d/mirrorlist.furros-bak
while read -r url; do
    base=${url%%\$repo*}
    # an unreachable mirror must not kill the build (set -e + pipefail): it just counts as stale;
    # an HTML error page full of digits is not a timestamp either
    stamp=$(curl -sf -m 15 "${base}lastupdate" | tr -dc 0-9) || stamp=
    [[ ${#stamp} -le 12 ]] || stamp=
    if [[ -z $stamp ]] || (( now - stamp > 3 * 86400 )); then
        log "  dropping stale mirror ${base} (lastupdate ${stamp:-?})"
        grep -vF "Server = $url" /etc/pacman.d/mirrorlist > /tmp/mirrorlist.new || true
        cat /tmp/mirrorlist.new > /etc/pacman.d/mirrorlist
    fi
done < <(sed -n 's/^Server = //p' /etc/pacman.d/mirrorlist)
grep -q '^Server' /etc/pacman.d/mirrorlist || { echo "no fresh mirror left" >&2; cp /etc/pacman.d/mirrorlist.furros-bak /etc/pacman.d/mirrorlist; exit 1; }
pacman -Syy --noconfirm >/dev/null

log "Fresh releng profile"
rm -rf "$PROFILE" "$WORK/work"
mkdir -p "$WORK/out"
cp -r /usr/share/archiso/configs/releng "$PROFILE"
# slow links: never give up on a download, fewer parallel streams
sed -i 's/^#\?ParallelDownloads.*/ParallelDownloads = 4/; /^\[options\]/a DisableDownloadTimeout' "$PROFILE/pacman.conf"

log "Overlay PurOS files"
cp -rT "$SRC/profile/airootfs" "$AIR"
mkdir -p "$AIR/usr/lib/furros"
cp -r "$SRC/src/installer" "$AIR/usr/lib/furros/installer"
cp -r "$SRC/src/academy" "$AIR/usr/lib/furros/academy"
cp -r "$SRC/src/drivers" "$AIR/usr/lib/furros/drivers"
cp -r "$SRC/src/wallpapers" "$AIR/usr/lib/furros/wallpapers"
find "$AIR/usr/lib/furros" -name __pycache__ -prune -exec rm -rf {} +
# Windows checkouts may carry CRLF: normalize every text file
grep -rIl $'\r' "$AIR" "$PROFILE/packages.x86_64" 2>/dev/null | xargs -r sed -i 's/\r$//'
cp "$SRC/profile/packages.extra" /tmp/furros-packages.extra
sed -i 's/\r$//' /tmp/furros-packages.extra

log "Terms of use as a file"
python3 - "$AIR/usr/lib/furros/installer" "$AIR/usr/share/furros/TERMS.md" <<'EOF'
import re, sys
sys.path.insert(0, sys.argv[1])
from terms import TERMS
md = []
for lang in ("ru", "en", "pl", "es"):
    t = TERMS[lang]
    t = re.sub(r"<h3>(.*?)</h3>", r"# \1", t)
    t = re.sub(r"</?b>", "**", t)
    t = re.sub(r"</?code>", "`", t)
    t = re.sub(r"</?p>", "", t)
    md.append(t.strip())
open(sys.argv[2], "w").write("\n\n---\n\n".join(md) + "\n")
EOF

log "Animated wallpapers and prebuilt packages"
mkdir -p "$AIR/usr/share/backgrounds/furros/animated" "$AIR/usr/share/furros/pkgs"
# Copy existing preview frames so every animated wallpaper card has a valid still frame even without external MP4s
shopt -s nullglob
for th in "$AIR"/usr/share/furros/thumbs/anim-*.png; do
    cp -f "$th" "$AIR/usr/share/backgrounds/furros/animated/$(basename "$th")"
done
for vdir in "$CUSTOM_VIDEOS_DIR" /root/videos/out; do
    if [[ -d $vdir ]]; then
        for vf in "$vdir"/*.mp4 "$vdir"/*.png; do
            cp -f "$vf" "$AIR/usr/share/backgrounds/furros/animated/"
        done
    fi
done

AUR_CACHE=/var/cache/furros-aur-pkgs
mkdir -p "$AUR_CACHE"
if [[ $BUILD_AUR_WALLS == 1 ]]; then
    pacman -S --needed --noconfirm cmake extra-cmake-modules ninja meson wayland-protocols mpv libx11 libxext libxrender qt6-base qt6-declarative qt6-multimedia >/dev/null 2>&1 || true
    for aur_pkg in mpvpaper xwinwrap-git plasma6-wallpapers-smart-video-wallpaper-reborn; do
        if ! compgen -G "$AUR_CACHE/${aur_pkg}-[0-9]*.pkg.tar.zst" >/dev/null; then
            log "  building $aur_pkg from AUR (cached for next runs)..."
            BDIR=/var/tmp/furros-aur-$aur_pkg
            rm -rf "$BDIR"
            if sudo -u builder git clone --depth 1 "https://aur.archlinux.org/${aur_pkg}.git" "$BDIR"; then
                (cd "$BDIR" && sudo -u builder makepkg -s --noconfirm) && cp -f "$BDIR"/*.pkg.tar.zst "$AUR_CACHE/" || log "  WARN: $aur_pkg build skipped"
            fi
            rm -rf "$BDIR"
        fi
    done
fi
for pf in "$AUR_CACHE"/*.pkg.tar.zst /root/pkgs/*.pkg.tar.zst; do
    [[ -f $pf ]] && cp -f "$pf" "$AIR/usr/share/furros/pkgs/"
done
shopt -u nullglob

log "Cursor theme"
mkdir -p "$AIR/usr/share/icons"
rm -rf "$AIR/usr/share/icons/FurrOS"
if [[ -f $SRC/tools/make_cursor.py ]]; then
    if [[ ! -f /var/tmp/cursor-out/white/FurrOS/index.theme ]]; then
        rm -rf /var/tmp/cursor-out/white
        mkdir -p /var/tmp/cursor-out/white
        python3 "$SRC/tools/make_cursor.py" --build white /var/tmp/cursor-out/white
    fi
    cp -a /var/tmp/cursor-out/white/FurrOS "$AIR/usr/share/icons/FurrOS"
else
    mkdir -p "$AIR/usr/share/icons/FurrOS"
    printf '[Icon Theme]\nName=FurrOS\nComment=PurOS Cursor Theme\nInherits=breeze_cursors\n' > "$AIR/usr/share/icons/FurrOS/index.theme"
fi

log "Wallpaper link, services"
ln -sf furros-lapa-wave.png "$AIR/usr/share/backgrounds/furros/current.png"
SD=$AIR/etc/systemd/system
mkdir -p "$SD/multi-user.target.wants"
ln -sf /usr/lib/systemd/system/sddm.service "$SD/display-manager.service"
ln -sf /usr/lib/systemd/system/NetworkManager.service "$SD/multi-user.target.wants/NetworkManager.service"
ln -sf /etc/systemd/system/furros-liveuser.service "$SD/multi-user.target.wants/furros-liveuser.service"
ln -sf /usr/lib/systemd/system/vboxservice.service "$SD/multi-user.target.wants/vboxservice.service"
# Deepin (DDE) does not pull xdg-desktop-autostart.target: start firstrun with its own session target
mkdir -p "$AIR/etc/systemd/user/dde-session-core.target.wants"
ln -sf ../furros-firstrun-dde.service "$AIR/etc/systemd/user/dde-session-core.target.wants/furros-firstrun-dde.service"
# Deepin without a window manager (kwin_x11 crashing) gets a dialog instead of silently broken windows
ln -sf ../furros-dde-wm-guard.service "$AIR/etc/systemd/user/dde-session-core.target.wants/furros-dde-wm-guard.service"
# NetworkManager replaces releng's networkd + iwd
rm -f "$SD/multi-user.target.wants/systemd-networkd.service" "$SD/multi-user.target.wants/iwd.service" \
      "$SD/sockets.target.wants/systemd-networkd.socket" \
      "$SD/network-online.target.wants/systemd-networkd-wait-online.service" \
      "$SD/systemd-networkd-wait-online.service.d" -r 2>/dev/null || true
rm -f "$SD/multi-user.target.wants/systemd-networkd.service"
# security: releng starts sshd for remote installs, but the live user furry/furry has passwordless sudo,
# so anyone on the same network could log in as root. PurOS installs locally: no SSH server in the live system,
# and no LLMNR (name spoofing on public Wi-Fi)
rm -f "$SD/multi-user.target.wants/sshd.service"
rm -f "$AIR/etc/ssh/sshd_config.d/10-archiso.conf"
mkdir -p "$AIR/etc/systemd/resolved.conf.d"
printf '[Resolve]\nLLMNR=no\nMulticastDNS=no\n' > "$AIR/etc/systemd/resolved.conf.d/80-furros-privacy.conf"

log "Identity"
echo puros > "$AIR/etc/hostname"
ln -sf furros-update "$AIR/usr/local/bin/puros-update"
ln -sf furros-drivers "$AIR/usr/local/bin/puros-drivers"
ln -sf furros-installer "$AIR/usr/local/bin/puros-installer"
printf 'LANG=ru_RU.UTF-8\n' > "$AIR/etc/locale.conf"
printf 'KEYMAP=us\nFONT=cyr-sun16\n' > "$AIR/etc/vconsole.conf"
cat > "$AIR/etc/pacman.d/hooks/40-locale-gen.hook" <<'EOF'
[Trigger]
Operation = Install
Type = Package
Target = glibc

[Action]
Description = Generating PurOS locales (en, ru, pl, es)...
When = PostTransaction
Depends = glibc
Depends = sh
Exec = /bin/sh -c "printf 'en_US.UTF-8 UTF-8\nru_RU.UTF-8 UTF-8\npl_PL.UTF-8 UTF-8\nes_ES.UTF-8 UTF-8\n' >> /etc/locale.gen && locale-gen"
EOF
sed -i 's/^HOOKS=(base udev /HOOKS=(base udev plymouth /' "$AIR/etc/mkinitcpio.conf.d/archiso.conf"
grep -q plymouth "$AIR/etc/mkinitcpio.conf.d/archiso.conf" || { echo "plymouth hook not added"; exit 1; }

log "Packages"
sed -i '/^virtualbox-guest-utils-nox$/d' "$PROFILE/packages.x86_64"
grep -v '^#' /tmp/furros-packages.extra | sed '/^$/d' >> "$PROFILE/packages.x86_64"
sort -u -o "$PROFILE/packages.x86_64" "$PROFILE/packages.x86_64"

log "profiledef"
P=$PROFILE/profiledef.sh
sed -i "s/^iso_name=.*/iso_name=\"${ISO_NAME}\"/; s/^iso_publisher=.*/iso_publisher=\"Custom Furry OS\"/;
        s/^iso_application=.*/iso_application=\"PurOS Live\"/; s/^iso_version=.*/iso_version=\"1.0\"/;
        s/^iso_label=.*/iso_label=\"FURROS_10\"/" "$P"
cat >> "$P" <<'EOF'
file_permissions+=(
  ["/usr/local/bin/furros-brand"]="0:0:755"
  ["/usr/local/bin/furros-firstrun"]="0:0:755"
  ["/usr/local/bin/furros-wallpaper"]="0:0:755"
  ["/usr/local/bin/furros-dde-wm-guard"]="0:0:755"
  ["/usr/local/bin/furros-set-wallpaper"]="0:0:755"
  ["/usr/local/bin/furros-wallpapers-app"]="0:0:755"
  ["/usr/local/bin/furros-pacman-hint"]="0:0:755"
  ["/usr/local/bin/furros-hypr-lua"]="0:0:755"
  ["/usr/lib/furros/wallpapers/furros_wallpapers.py"]="0:0:755"
  ["/usr/local/bin/furros-liveuser"]="0:0:755"
  ["/usr/local/bin/furros-installer"]="0:0:755"
  ["/usr/local/bin/furros-labwc-fix"]="0:0:755"
  ["/usr/local/bin/furros-academy"]="0:0:755"
  ["/usr/local/bin/furros-welcome"]="0:0:755"
  ["/usr/local/bin/furros-about"]="0:0:755"
  ["/usr/local/bin/furros-lapa"]="0:0:755"
  ["/usr/lib/furros/academy/furros_about.py"]="0:0:755"
  ["/usr/lib/furros/academy/furros_lapa.py"]="0:0:755"
  ["/etc/grub.d/11_furros_safe_graphics"]="0:0:755"
  ["/etc/grub.d/45_furros_power"]="0:0:755"
  ["/usr/lib/furros/academy/furros_welcome.py"]="0:0:755"
  ["/usr/local/bin/furros-drivers"]="0:0:755"
  ["/usr/local/bin/furros-update"]="0:0:755"
  ["/usr/local/bin/furros-gpu-soft"]="0:0:755"
  ["/usr/local/bin/furros-desktop"]="0:0:755"
  ["/usr/local/bin/furros-picom"]="0:0:755"
  ["/usr/lib/systemd/user-environment-generators/60-furros-vm-graphics"]="0:0:755"
  ["/usr/lib/furros/drivers/furros_hw.py"]="0:0:755"
  ["/usr/lib/furros/installer/backend.py"]="0:0:755"
  ["/usr/lib/furros/installer/furros_installer.py"]="0:0:755"
  ["/usr/lib/furros/academy/furros_academy.py"]="0:0:755"
  ["/usr/share/furros/skel/bspwm/.config/bspwm/bspwmrc"]="0:0:755"
)
EOF

log "Boot menus"
for f in $(grep -rl "Arch Linux" "$PROFILE"/efiboot "$PROFILE"/grub "$PROFILE"/syslinux 2>/dev/null); do
    sed -i 's/Arch Linux/FurrOS/g' "$f"
done
for f in $(grep -rl "archisobasedir=" "$PROFILE"/efiboot "$PROFILE"/grub "$PROFILE"/syslinux 2>/dev/null); do
    sed -i '/archisobasedir=/ { /quiet splash/! s/$/ quiet splash/ }' "$f"
done
cp "$SRC/profile/airootfs/usr/share/furros/splash.png" "$PROFILE/syslinux/splash.png" 2>/dev/null || true
# Live boot menus: the first entry boots with full graphics, a second "safe graphics" entry adds nomodeset.
# Until 25.11 every live entry had nomodeset, and integrated GPUs on old BIOS laptops got no picture at all
# (tester reports). UEFI entries, BIOS syslinux and the GRUB menus; English titles: firmware fonts often have
# no Cyrillic. The installed system writes its own kernel command line (FurrBoot keeps its own safe entry).
python3 "$SRC/tools/live_safe_entries.py" "$PROFILE" || { log "FAILED: live safe-graphics entries"; exit 1; }

# FurrBoot instead of systemd-boot for UEFI (bootloader/furros/furrboot.efi, or FURRBOOT_EFI=/path). It reads the
# same Boot Loader Specification entries; the extra furrboot-* keys only give them names and pictures.
# BIOS (syslinux) and Ventoy (grub/loopback.cfg) stay as they are. FURRBOOT_TEST=1: the ISO gets its own name.
# FURRBOOT_EFI=none: systemd-boot as before.
if [[ $BUILD_FURRBOOT == 1 && ! -f $SRC/bootloader/furros/furrboot.efi && -f $SRC/bootloader/furrboot/Cargo.toml ]]; then
    log "Compiling FurrBoot UEFI bootloader (Rust)..."
    pacman -S --needed --noconfirm rustup >/dev/null 2>&1 || true
    sudo -u builder rustup default stable >/dev/null 2>&1 || true
    sudo -u builder rustup target add x86_64-unknown-uefi >/dev/null 2>&1 || true
    CARGO_TGT=/var/tmp/furros-cargo-target
    mkdir -p "$CARGO_TGT" && chown -R builder: "$CARGO_TGT" "$SRC/bootloader/furrboot"
    if (cd "$SRC/bootloader/furrboot" && sudo -u builder env CARGO_TARGET_DIR="$CARGO_TGT" cargo build --release --target x86_64-unknown-uefi); then
        cp -f "$CARGO_TGT/x86_64-unknown-uefi/release/furrboot.efi" "$SRC/bootloader/furros/furrboot.efi"
    else
        log "WARN: FurrBoot build failed, falling back to systemd-boot / GRUB"
    fi
fi
[[ $BUILD_FURRBOOT == 0 ]] && FURRBOOT_EFI=none
[[ -z ${FURRBOOT_EFI:-} && -f $SRC/bootloader/furros/furrboot.efi ]] && FURRBOOT_EFI=$SRC/bootloader/furros/furrboot.efi
[[ ${FURRBOOT_EFI:-} == none ]] && FURRBOOT_EFI=""
if [[ -n ${FURRBOOT_EFI:-} ]]; then
    log "FurrBoot: UEFI menu of the live ISO = $FURRBOOT_EFI"
    E=$PROFILE/efiboot/loader/entries
    sed -i -E 's/^title .*/title    PurOS Live/' "$E/01-archiso-linux.conf"
    printf '%s\n' "furrboot-kind main" "furrboot-subtitle попробовать и установить" \
        "furrboot-when обычный запуск" >> "$E/01-archiso-linux.conf"
    if [[ -f $E/03-archiso-linux-safe.conf ]]; then
        sed -i -E 's/^title .*/title    PurOS Live — безопасная графика/' "$E/03-archiso-linux-safe.conf"
        printf '%s\n' "furrboot-kind safe" "furrboot-when чёрный экран? запуск без драйвера видеокарты (nomodeset)" \
            >> "$E/03-archiso-linux-safe.conf"
    fi
    if [[ -f $E/02-archiso-speech-linux.conf ]]; then
        sed -i -E 's/^title .*/title    PurOS Live с озвучкой/' "$E/02-archiso-speech-linux.conf"
        printf '%s\n' "furrboot-kind kernel" "furrboot-when озвучка экрана для незрячих" >> "$E/02-archiso-speech-linux.conf"
    fi
    printf '%s\n' "# FurrBoot on the live medium" "timeout 10" "default 01-archiso-linux" "lang ru" "theme comic" \
        "tz-offset 0" > "$PROFILE/efiboot/loader/furrboot.conf"
    [[ ${FURRBOOT_TEST:-} == 1 ]] && sed -i 's/^iso_version=.*/iso_version="1.0-furrboot-test"/' "$PROFILE/profiledef.sh"
    # mkarchiso copies systemd-boot from the image and only loader.conf from loader/: a patched copy of it
    # also takes furrboot.conf and puts FurrBoot at \EFI\BOOT\BOOTX64.EFI (IA32 mixed mode keeps systemd-boot)
    # (mkarchiso names it BOOT${target_arch}.EFI with target_arch "x64", lowercase: the El Torito/USB EFI image
    # gets its EFI/ copied from there, so this one file is what every UEFI PC starts from the stick)
    MKARCHISO=$WORK/mkarchiso-furrboot
    sed -e '/"${isofs_dir}\/EFI\/BOOT\/BOOT${target_arch}.EFI"$/a\    if [[ "${target_arch,,}" == x64 ]]; then install -m 0644 -- "'"$FURRBOOT_EFI"'" "${isofs_dir}/EFI/BOOT/BOOT${target_arch}.EFI"; fi' \
        -e '/install -m 0644 -- "${profile}\/efiboot\/loader\/loader.conf" "${work_dir}\/loader"$/a\    if [[ -e "${profile}/efiboot/loader/furrboot.conf" ]]; then install -m 0644 -- "${profile}/efiboot/loader/furrboot.conf" "${work_dir}/loader"; fi' \
        /usr/bin/mkarchiso > "$MKARCHISO"
    [[ $(grep -c 'furrboot' "$MKARCHISO") -eq 2 ]] || { echo "patching mkarchiso failed" >&2; exit 1; }
    chmod +x "$MKARCHISO"
else
    MKARCHISO=mkarchiso
fi

log "List of PurOS files the installer copies to the new system"
(
  cd "$AIR"
  for p in usr/lib/furros/academy usr/lib/furros/drivers usr/local/bin/furros-drivers usr/local/bin/furros-update usr/local/bin/furros-brand usr/local/bin/furros-firstrun usr/local/bin/furros-wallpaper usr/local/bin/furros-set-wallpaper usr/local/bin/furros-wallpapers-app usr/local/bin/furros-pacman-hint usr/lib/furros/wallpapers usr/local/bin/furros-academy usr/local/bin/furros-welcome usr/local/bin/furros-labwc-fix \
           usr/local/bin/furros-about usr/local/bin/furros-lapa usr/share/applications/furros-about.desktop etc/xdg/autostart/furros-lapa.desktop \
           usr/share/furros usr/share/backgrounds/furros usr/share/plymouth/themes/furros usr/share/grub/themes/furros \
           usr/share/sddm/themes/furros usr/share/color-schemes/FurrOS.colors usr/share/konsole/FurrOS.profile \
           usr/share/konsole/FurrOS.colorscheme usr/share/fonts/furros usr/share/pixmaps/furros.png \
           usr/share/applications/furros-academy.desktop usr/share/applications/furros-welcome.desktop usr/share/applications/furros-wallpapers.desktop \
           usr/share/plasma/shells/org.kde.plasma.desktop/contents/updates/furros-defaults.js \
           etc/xdg/autostart/furros-firstrun.desktop etc/xdg/autostart/furros-polkit-agent.desktop etc/grub.d/11_furros_safe_graphics etc/grub.d/45_furros_power etc/xdg/fastfetch etc/xdg/konsolerc etc/xdg/ksplashrc etc/xdg/kcminputrc \
           usr/share/icons/FurrOS usr/share/sounds/furros etc/profile.d/furros-cursor.sh etc/profile.d/furros-terminal.sh etc/profile.d/furros-vm-graphics.sh etc/profile.d/furros-desktop.sh usr/local/bin/furros-desktop usr/lib/systemd/user-environment-generators/60-furros-vm-graphics usr/local/bin/furros-gpu-soft usr/local/bin/furros-picom etc/skel/.icons usr/share/plasma/look-and-feel/org.furros.desktop etc/furros/bashrc \
           etc/pacman.d/hooks/60-furros-branding.hook etc/pacman.d/hooks/61-furros-hyprland-lua.hook usr/local/bin/furros-hypr-lua etc/librewolf/librewolf.overrides.cfg etc/skel/.librewolf etc/skel/.config usr/share/wallpapers/FurrOS* usr/share/plasma/avatars/furros-* usr/share/pixmaps/faces/furros-* \
           etc/dconf/db/gdm.d/01-furros etc/dconf/profile/gdm \
           etc/systemd/user/furros-firstrun-dde.service etc/systemd/user/dde-session-core.target.wants \
           etc/systemd/user/furros-dde-wm-guard.service etc/systemd/user/dde-session@x11.service.d usr/local/bin/furros-dde-wm-guard; do
      [[ -e $p ]] && echo "/$p"
  done
  for i in usr/share/icons/hicolor/*/apps/furros.png; do echo "/$i"; done
) > "$AIR/usr/share/furros/files.list"
wc -l "$AIR/usr/share/furros/files.list"

log "Building local package repository"
REPO_TMP=$WORK/furros-repo-out
bash "$SRC/tools/build_repo.sh" "$AIR" "$REPO_TMP"
if [[ ${REPO_ONLY:-} == 1 ]]; then log "REPO_ONLY: repository built, no ISO"; exit 0; fi
mkdir -p "$AIR/usr/share/furros/repo"
for f in "$REPO_TMP"/*; do
    n=$(basename "$f"); n=${n%.sig}
    [[ -e /root/repo-extra/$n ]] || cp "$f" "$AIR/usr/share/furros/repo/"
done
# the videos now ship in furros-wallpapers: the live session itself only needs their still frames
rm -f "$AIR"/usr/share/backgrounds/furros/animated/*.mp4

log "mkarchiso (building bootable ISO, this takes 10-25 minutes)"
mkdir -p "$OUT_DIR"
rm -f "$WORK"/out/*.iso "$WORK"/out/*.iso.sig "$WORK"/out/*.iso.sha256
"$MKARCHISO" -v -r -w "$WORK/work" -o "$WORK/out" "$PROFILE"

log "Checksum and local signature"
KEY=$(gpg --list-secret-keys --with-colons | awk -F: '$1=="fpr"{print $10; exit}')
cd "$WORK/out"
for iso in *.iso; do
    sha256sum "$iso" > "$iso.sha256"
    if [[ -n $KEY ]]; then
        gpg --batch --yes --detach-sign -u "$KEY" "$iso"
        gpg --verify "$iso.sig" "$iso"
    fi
    if [[ "$OUT_DIR" != "$WORK/out" ]]; then
        cp -f "$iso" "$iso.sha256" "$OUT_DIR/"
        [[ -f "$iso.sig" ]] && cp -f "$iso.sig" "$OUT_DIR/"
    fi
done
rm -f /etc/sudoers.d/99-furros-builder
ls -lh "$OUT_DIR"
log "Done! ISO saved to: $OUT_DIR"
