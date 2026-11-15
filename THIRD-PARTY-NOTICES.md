# Third-party notices

MacTune itself is licensed under the GNU General Public License v3.0 (see `LICENSE`).
It includes or depends on the following third-party work.

## Bundled in this repository

| Component | Where | License | Source |
|---|---|---|---|
| **AppleT2Smc** PawnIO module (compiled `.bin` + Pawn source `.p`) | `drivers/` | LGPL-2.1-or-later | [golirt1/RPMAC](https://github.com/golirt1/RPMAC) (`src/pawnio/AppleT2Smc.p`, `dist/AppleT2Smc.bin`) |

The module is shipped unmodified. Its source is included next to the binary (`drivers/AppleT2Smc.p`),
so you can rebuild or replace it as the LGPL requires.

## NuGet dependencies (downloaded at build time)

| Package | License |
|---|---|
| [LibreHardwareMonitorLib](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor) | MPL-2.0 |
| [HidSharp](https://www.zer7.com/software/hidsharp) | Apache-2.0 |
| System.Management (.NET) | MIT |

## Not bundled – installed separately by the user

| Component | Why | Source |
|---|---|---|
| **PawnIO** 2.0.1 (Unrestricted) | Kernel driver needed for fan control and CPU power/clock readings on T2 Macs | [namazso/PawnIO.Setup](https://github.com/namazso/PawnIO.Setup) |

## Protocol references

No code was copied from these projects; they documented how the hardware talks, which made MacTune possible.

- [golirt1/RPMAC](https://github.com/golirt1/RPMAC) – T2 SMC access through PawnIO, fan key behaviour on MacBookPro16,1
- [ozaretskyi/SimpleMacFan](https://github.com/ozaretskyi/SimpleMacFan) – Apple MacHALDriver SMC IOCTLs (pre-T2 Macs)
- [charlie754/mac-fan-control-windows](https://github.com/charlie754/mac-fan-control-windows) – AppleSMC device protocol, SMC data types
- [mksolutionsuniversal/BootCampPerformanceControl](https://github.com/mksolutionsuniversal/BootCampPerformanceControl) – verified T2 fan key families
- Linux kernel `drivers/hid/hid-apple.c` – keyboard backlight HID reports on T2 MacBooks
