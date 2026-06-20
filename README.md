# WinSshCopyId

A small Windows desktop app (WinUI 3) that installs your SSH **public** key on
one or more Linux hosts so you can log in **without typing a password** every
time. Think of it as a friendly, batch-capable GUI version of the classic
`ssh-copy-id` command.

You give it the host(s), your username, and your password **once**. It logs in,
appends your public key to each server's `~/.ssh/authorized_keys`, fixes the file
permissions, and then verifies that key-based login actually works. From then on
`ssh user@host` just lets you in.

![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D6)
![.NET 8](https://img.shields.io/badge/.NET-8-512BD4)
![License: MIT](https://img.shields.io/badge/License-MIT-green)

---

## Features

- **One-click key install** - the `ssh-copy-id` workflow with a GUI.
- **Multiple hosts at once** - paste a list of hosts (one per line, optional
  `host:port`) and deploy the same key with the same credentials to all of them.
- **Bring your own key or generate one** - picks up existing keys in `~/.ssh`,
  or generates a fresh `ed25519` key with `ssh-keygen` (falls back to an
  in-process RSA-3072 generator if OpenSSH tools are not installed).
- **Save your credentials** - optionally remember the hosts, username and key,
  and (if you choose) the password, encrypted with Windows DPAPI so only your
  Windows account on this machine can read it.
- **Import / export profiles** - move your host list and settings between
  machines as a portable `.json` file (passwords are never exported).
- **Idempotent** - never adds a duplicate line to `authorized_keys`.
- **Verifies for you** - after installing, it logs in with the key to confirm
  passwordless access really works, per host.
- **Optional `~/.ssh/config` entries** - so you can connect with `ssh myhost`.
- **No telemetry, no network calls** except the SSH connections you ask for.

## Requirements

- Windows 10 (1809 / build 17763+) or Windows 11
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- To **build**: Visual Studio 2022+ with the *Windows App SDK C# Templates* /
  *.NET Desktop* workload (this provides the WinUI resource compiler that the
  bare `dotnet` CLI does not ship). Building with `msbuild` works too.
- To **run** a framework-dependent build: the
  [Windows App Runtime 1.6](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads).
  The self-contained build below bundles it, so end users need nothing extra.

## Build and run

### Visual Studio (simplest)

Open `WinSshCopyId.sln`, set the platform to **x64**, and press **F5**.

### Command line (MSBuild)

```powershell
git clone https://github.com/cafepromenade/WinSshCopyId.git
cd WinSshCopyId
msbuild src/WinSshCopyId/WinSshCopyId.csproj -t:Build -p:Configuration=Debug -p:Platform=x64 -restore
```

> Note: `dotnet build` / `dotnet run` only work for WinUI 3 if the Visual Studio
> WinUI build tools are installed; otherwise use `msbuild` as shown above.

## Publish a standalone .exe (no prerequisites)

This produces a self-contained build that needs **no** .NET runtime and **no**
Windows App Runtime installed on the target machine:

```powershell
msbuild src/WinSshCopyId/WinSshCopyId.csproj -t:Build `
  -p:Configuration=Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 `
  -p:WindowsAppSDKSelfContained=true -p:SelfContained=true -restore
```

The app appears under
`src/WinSshCopyId/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/WinSshCopyId.exe`.

## How to use

1. Enter your **hosts**, one per line (optionally `host:port`), a **username**
   and **default port** shared by all of them, and the account **password**.
2. Pick an existing key from the dropdown, **Browse...** to a `.pub` file, or
   click **Generate new** to create one.
3. (Optional) Tick **Add a ~/.ssh/config entry for each host**, or
   **Remember** / **save password** to persist your settings.
4. Click **Install key on all hosts**. When it finishes you get a per-host
   summary with the exact `ssh` command for each, and a **Copy commands** button.

Use **Test all** any time to check whether key-based login already works on every
listed host. Use **Export profile** / **Import profile** to carry your host list
between machines.

## How it works

After authenticating with your password (plain `password` or
`keyboard-interactive`), the app runs one idempotent remote command per host:

```sh
umask 077
mkdir -p ~/.ssh && chmod 700 ~/.ssh
touch ~/.ssh/authorized_keys && chmod 600 ~/.ssh/authorized_keys
grep -qxF "$KEY" ~/.ssh/authorized_keys || printf '%s\n' "$KEY" >> ~/.ssh/authorized_keys
```

Then it reconnects using the private key to confirm passwordless login.

## Security notes

- Your password is held only in memory for the install. If you do not tick
  **save password**, it is cleared from the input box afterwards. When you do
  save it, it is encrypted with Windows DPAPI (`CryptProtectData`,
  current-user scope) - never written or logged in plain text.
- **Exported profiles never contain a password**, even an encrypted one (a DPAPI
  blob is useless on another machine anyway).
- Private keys are never transmitted - only the public key is sent to the server.
- The server's host-key fingerprint (SHA256) is shown in the log on first
  connect (trust-on-first-use, the same model `ssh-copy-id` uses). For a
  high-security setup, verify that fingerprint against the server out of band.
- Generated private keys are written to `~/.ssh` and locked down to your account.

## License

[MIT](LICENSE) (c) 2026 cafepromenade
