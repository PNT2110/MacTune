# Enabling fan control (T2 Macs)

Everything else in MacTune works without this. Do it only if you want to control the fans.

On Macs with the T2 chip (2018–2020) the SMC sits behind the T2, and Apple's Boot Camp drivers do not
expose it. MacTune reaches it through the [PawnIO](https://github.com/namazso/PawnIO.Setup) kernel driver
with the `AppleT2Smc` module from [RPMac](https://github.com/golirt1/RPMAC). That module is **not signed**, so you need:

- PawnIO **2.0.1**, installed in **Unrestricted** mode (2.1.0 and 2.2.0 do not work)
- Windows **Test Signing** turned on

## Read this first

- Test Signing plus PawnIO Unrestricted **lowers Windows security**: a malicious program that already has
  administrator rights can load kernel code more easily. Do not run unknown software as administrator.
- Games with kernel anti-cheat (Valorant, FACEIT, some others) will refuse to start.
- A "Test Mode" watermark appears in the bottom-right corner of the desktop. That is expected.
- If BitLocker is on, have your recovery key ready before changing boot settings.
- The module can only write fan keys (`F?Md`, `F?Tg`, `FS!`). It cannot change anything else in the SMC.

## Step 1 – Turn on Test Signing

Open **Command Prompt as administrator** and run:

```
bcdedit /set testsigning on
```

- "The operation completed successfully" → restart the Mac and continue with step 2.
- An error mentioning **Secure Boot** → boot into macOS Recovery (hold ⌘+R at startup), open
  **Utilities → Startup Security Utility**, set Secure Boot to **No Security**, restart into Windows and run the command again.

## Step 2 – Install PawnIO 2.0.1 Unrestricted

1. Download <https://github.com/namazso/PawnIO.Setup/releases/download/2.0.1/PawnIO_setup.exe>
2. Check the file (PowerShell, in your Downloads folder):

   ```powershell
   Get-FileHash .\PawnIO_setup.exe
   ```

   The SHA256 must be `3A34B5DF231F10F252C4B50D3C777534B2A2CEC5E1DF9466AB2DCA401C068C44`. If it differs, delete the file.
3. In an administrator Command Prompt:

   ```
   "%USERPROFILE%\Downloads\PawnIO_setup.exe" -install -unrestricted
   ```

4. Restart the Mac.

## Step 3 – Use it

1. Quit other fan tools (RPMac, Macs Fan Control). Only one program can talk to the SMC at a time.
2. Open MacTune → **Fans & Heat**. If you see the RPM of both fans, it is connected.
3. Pick a fan mode. **Auto** hands control back to Apple at any time.

## Safety built into MacTune

- Targets are always clamped to each fan's own minimum and maximum.
- CPU at 95 °C or more → fans go to maximum until it drops below 85 °C.
- 20 seconds after taking control, MacTune checks that the fans really follow; if not, it returns them to Apple.
- Quitting MacTune, a crash, or Windows shutting down → fans return to Apple auto.
- If MacTune was killed while fans were manual, the next start returns them to auto.

## Undo everything

1. In MacTune choose fan mode **Auto**, then quit MacTune (right-click the tray icon → Quit).
2. Settings → Apps → Installed apps → **PawnIO** → Uninstall.
3. Administrator Command Prompt: `bcdedit /set testsigning off`
4. If you changed Secure Boot in step 1, set it back to **Full Security** in macOS Recovery.
5. Restart.

If the fans ever stay stuck at high speed: start MacTune again (it resets them), or reset the SMC –
shut down, hold **right Shift + left Option + left Control** for 7 seconds, keep holding and add the
**power button** for 7 more seconds, release, wait a few seconds and power on.
