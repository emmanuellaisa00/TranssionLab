# Security, Ethical Servicing & Anti-Bypass Policy

TranssionLab is engineered strictly for legitimate device diagnostics, authorized software maintenance, verified firmware inspection, and officially permitted developer workflows on TECNO, Infinix, and itel Android devices.

## Explicitly Prohibited Capabilities

TranssionLab **does not** and **will not** implement:
1. **Boot ROM (BROM) or Preloader Exploits:** No USB control-transfer buffer overflows (e.g., kamakiri/carbonara) or Watchdog/Crypto-Engine manipulation to disable MediaTek SLA/DAA.
2. **Unisoc FDL Signature Bypasses:** No crafted FDL payloads to bypass Unisoc Secure Boot verification.
3. **Anti-Theft / FRP Circumvention:** No erasure or manipulation of `frp`, `persistent`, or Factory Reset Protection partitions.
4. **Protected Radio & Identity Tampering:** Reading or writing IMEI, MEID, MAC addresses, Widevine keys, `nvram`, `nvdata`, `proinfo`, `seccfg`, `prodnv`, or `l_fixnv*` is blocked at the protocol and Flash Manager layers.
5. **Credential or User Data Extraction:** No bypassing Android lockscreens, file-based encryption (FBE), or unauthorized ADB debugging prompts.
6. **Unauthorized Bootloader Unlocking:** Bootloader unlocking is restricted exclusively to official `fastboot flashing unlock` / `fastboot oem unlock` workflows when permitted by the device's OEM Unlocking toggle and bootloader policy.
