# Noexs-Sharp

**Version 2.0.1** · A rewrite of [Noexes](https://github.com/mdbell/Noexes), the Nintendo Switch
memory search and debugging tool.

- **App** (`App/`): the PC program, a single self-contained executable for Windows, Linux and macOS
  (x64 and ARM64), with dark mode.
- **Sysmodule** (`sysmodule/`): runs on the Switch (Atmosphère) and talks to the App over Wi-Fi or USB.

App and sysmodule belong together: the App only connects to a 2.x sysmodule.

## Download

Get the files from [Releases](../../releases):

| File | For |
| --- | --- |
| `noexs-sysmodule.zip` | the Switch (SD card) |
| `noexs-app-win-x64.zip` / `-win-arm64.zip` | Windows |
| `noexs-app-linux-x64.tar.gz` / `-linux-arm64.tar.gz` | Linux |
| `noexs-app-osx-arm64.tar.gz` / `-osx-x64.tar.gz` | macOS (Apple Silicon / Intel) |

macOS: the binary isn't signed, so run `xattr -d com.apple.quarantine noexs` once after unpacking.

## Install

1. Unzip `noexs-sysmodule.zip` to the root of the SD card. It contains
   `atmosphere/contents/4200000000004E58/` (`exefs.nsp`, `flags/boot2.flag`, `toolbox.json`).
2. If an older noexs is installed, delete `atmosphere/contents/054e4f4558454000`.
3. Reboot the Switch.

## Connect

- **Wi-Fi:** enter the Switch's IP address and press **Connect** (TCP port 20056). Recently used
  addresses are under **▾** next to the IP box.
- **USB:** pick **USB** next to the IP box and press **Connect**. On Windows this needs the
  libusbK driver once (below).

The status bar then shows `Connected (noexs 2.0.1)`.

### USB driver on Windows (libusbK, once)

1. Connect the Switch directly to the PC (not through the dock). With noexs running it appears as
   **057E:3000**.
2. Open [Zadig](https://zadig.akeo.ie/), choose **Options → List All Devices**, select **057E 3000**.
3. Pick **libusbK** and click **Install Driver** / **Replace Driver**.

It's the same driver DBI, NS-USBloader and Goldleaf use. On Linux and macOS no driver is needed
(libusb; on Linux a udev rule may be needed to use it without root).

## Features

| Tab | What it does |
| --- | --- |
| Tools | processes with game names (from the console), attach, memory regions |
| Search | value searches and memory dumps |
| Pointer Search | finds pointer paths like `[[main+XX]+YY]+ZZ` (PointerSearcher) |
| Memory Viewer | view and edit memory |
| Watch List | watch and freeze values, also by pointer path |
| Disassembler | ARM64 disassembler and assembler (built in) |
| Signatures | turns an address or pointer path into a byte signature that still finds it after a game update |
| Dump Parser | inspects `.dmp` files offline and evaluates pointer paths in them |
| Utils | USB devices, **Dump Transfer** options (compression LZ4 / RLE / off, double buffering) and the **Switch Log** (what the sysmodule is doing) |

## Troubleshooting

| Problem | Fix |
| --- | --- |
| Can't connect over Wi-Fi | Same network? Sysmodule installed and the Switch rebooted? |
| "sysmodule … needs 2.x" | Install the sysmodule from the same release as the App. |
| Only 057E:2000 visible on USB | noexs' USB mode isn't active: check **Utils → Switch Log** (over Wi-Fi). `sdmc:/config/noexs/usb_disabled.flag` turns USB off. |
| USB driver messages | Install or replace the driver for 057E:3000 with libusbK (see above). |

The sysmodule also logs to `sdmc:/config/noexs/log.txt`.

## Building

**App** (.NET 10 SDK; Windows needs the C++ build tools, Linux `clang` and `zlib1g-dev`, macOS Xcode):

```
dotnet publish App/Noexs -c Release -r win-x64 -o App/publish
```

Other targets: `win-arm64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64` (Linux and macOS
builds must run on that OS).

**Sysmodule** (devkitPro with libnx):

```
make -C sysmodule
```

The SD card files end up in `sysmodule/out/`.

Every push to `main` is built by GitHub Actions and published as a pre-release; pushing a `v*` tag
publishes a regular release.

## Credits

Based on [Noexes](https://github.com/mdbell/Noexes) by mdbell (GPL-3.0). Pointer search from
[PointerSearcher](https://github.com/Takumi4685/PointerSearcher) by Takumi4685; the Dump Parser
follows [NoExesDumpParse](https://github.com/rydoginator/NoExesDumpParse) by rydoginator. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
