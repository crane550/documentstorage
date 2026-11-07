# Ollama on a Windows desktop

Install [Ollama for Windows](https://ollama.com/download/windows). Update the NVIDIA driver if needed. Open a new PowerShell window after installation.

## Allow your Stash server to connect

Set these for the Windows account running Ollama:

```powershell
[Environment]::SetEnvironmentVariable("OLLAMA_HOST", "0.0.0.0:11434", "User")
[Environment]::SetEnvironmentVariable("OLLAMA_KEEP_ALIVE", "2m", "User")
[Environment]::SetEnvironmentVariable("OLLAMA_NUM_PARALLEL", "1", "User")
```

Quit Ollama through its tray icon and reopen it from Start. These settings take effect in the restarted application. The user environment settings do not configure an Ollama process running under a different service account.

On your trusted home LAN, set the Windows network profile to **Private**. In PowerShell **as administrator**, add a firewall rule:

```powershell
New-NetFirewallRule `
    -DisplayName "Ollama - Private LAN" `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort 11434 `
    -Profile Private `
    -RemoteAddress LocalSubnet
```

Ollama's direct API has no application login. This rule permits your private local subnet; you can narrow `RemoteAddress` to your Stash server's reserved LAN address instead. Do not expose port 11434 through a router or public Cloudflare tunnel. No browser CORS setting is needed: Stash's server makes the requests.

## Install and check a model

Start with the same model installed on your existing reader, then compare larger models separately. For the current small-model setup:

```powershell
ollama pull qwen3:4b-instruct
ollama run qwen3:4b-instruct "Reply with exactly READY"
ollama ps
```

`ollama ps` shows whether the loaded model is using GPU, CPU, or both. Inspect it immediately after the test, before idle unloading. GPU acceleration depends on the installed driver, Ollama version and model fitting in VRAM; installing Ollama alone does not prove GPU use. Windows Task Manager's dedicated GPU memory and CUDA/compute graph can help confirm it.

## Check from the Stash server

Replace `YOUR-DESKTOP` with the computer's LAN name or IPv4 address:

```powershell
Invoke-RestMethod "http://YOUR-DESKTOP:11434/api/tags" |
    Select-Object -ExpandProperty models |
    Select-Object name, size
```

If the name fails, use the active adapter's IPv4 address from `ipconfig` on the desktop. A router DHCP reservation keeps it stable. If the connection fails, check that Ollama was restarted, the network is Private, the firewall rule applies, and the PC is awake.

In **Settings › AI reader**, add the desktop as a reader, check its connection, select the installed model, and move it to the preferred position. Keep the existing server/model as fallback and save. Saving reader settings stops pending work; completed readings stay saved. Use **Read again with AI** explicitly for a document you want to test with the new setup.

The desktop can sleep normally. Stash skips unavailable readers, retains waiting document jobs when none are available, and checks again automatically. It does not currently wake computers. The tray application must be running in the logged-in account; verify Windows startup behavior if you want it available after a reboot. Stash's own idle-unload setting overrides Ollama's default for requests made by Stash.
