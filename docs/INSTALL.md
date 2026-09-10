# Installing

quickshell ships as one archive, `quickshell-<version>-win-x64.zip`, with `SHA256SUMS.txt` beside
it. It needs Windows 10 or 11 on x64 and nothing else — the .NET runtime is inside the archive.

Check a download against the sums before running it:

```
certutil -hashfile quickshell-0.1.0-win-x64.zip SHA256
```

**The archive is not signed yet**, so SmartScreen warns on its first start. Signing is owed before
the first release rather than after it, and until it lands a build says so in its own name:
`...-unsigned.zip`.

## Run it without installing

Unzip it anywhere — Downloads, a USB stick — and start `quickshell.exe` in the folder it makes.

That copy is **portable**. The file `quickshell.portable` beside the executable keeps its settings,
saved sessions and logs in `data\` next to it, and nothing is written to your profile. Deleting the
folder removes all of it.

## Install it for yourself

From that copy, open the palette with `Ctrl+Shift+P` and choose **Install quickshell for this
user**, or run:

```
quickshell.exe --install
```

There is no administrator prompt. The program goes to `%LocalAppData%\Programs\quickshell`,
`quickshell` appears in your Start menu, and it is listed under **Installed apps** in Settings.

- The installed copy keeps its settings in `%AppData%\quickshell`. The copy you installed from keeps
  its own in its `data` folder, and still works.
- Installing a newer archive the same way replaces the installed copy whole — nothing of the old
  version is left beside the new one.
- If the installed copy is running, you are asked to close it first rather than getting half of
  each version.

## Install it for every user

For a managed deployment, from an elevated prompt:

```
quickshell.exe --install --all-users --quiet
```

That is `C:\Program Files\quickshell`, every user's Start menu, and an entry under
`HKEY_LOCAL_MACHINE`. Asked without an administrator, it exits with 740 — *the requested operation
requires elevation* — which is the code deployment tools already read that way. Without `--quiet` it
asks Windows for the administrator instead.

## Uninstall it

From **Installed apps** in Settings, or `quickshell.exe --uninstall` from the installed copy. It
removes the program folder, the Start menu shortcut and the entry, and then asks whether to remove
your settings and saved sessions too — they are kept unless you say yes, so a later install finds
them. `--uninstall --quiet`, which is what a deployment runs, never removes them.

## What it does to your machine, all of it

- A folder of program files, a Start menu shortcut, and one entry in the list of installed apps.
- No service, no scheduled task, no file association, no change to `PATH`.
- It does not check for updates yet.

## Exit codes

| Code | Meaning |
|---|---|
| `0` | Done. |
| `1` | Not done. Without `--quiet` a dialog said why. |
| `740` | Installing or removing it for every user needs an administrator. |
