# Kurobane for Windows

Kurobane (黒羽, "black wing") is a native Windows desktop client for a **ZeroClaw gateway** that you already run
somewhere else — a home server, a VPS, or another PC. Kurobane runs on your Windows PC and talks to the gateway over
the network; it does not run an agent itself.

It is an independent, community-built client, inspired by [Hikyaku](https://github.com/BookahCoding/Hikyaku)
(the Hermes Agent equivalent). It is not made by or affiliated with the ZeroClaw project.

## What it does

- **Chat** with your ZeroClaw agent over the gateway WebSocket (`/ws/chat`): streaming replies (`chunk` frames),
  tool calls and results shown live, approval prompts answered from the app, token/cost summary on `done`.
- **Sessions**: list gateway sessions (`GET /api/sessions`), load their transcript, create new chats, rename,
  delete, and abort a running turn (`POST /api/sessions/{id}/abort`).
- **Gateway config**: view the masked config and the agent list (`GET /api/config`), and change a single property
  with `PUT /api/config/prop?path=…` (the gateway masks secrets; the app refuses to write the masked placeholder back).
- **Pairing**: exchange a pairing code for a bearer token (`POST /api/pair`); the token is stored on your PC
  encrypted with Windows DPAPI, never in plain settings.
- Dark/light/system appearance, three text sizes.

## Requirements

- Windows 10 or 11, 64-bit (x64).
- A running ZeroClaw gateway reachable from your PC — on your LAN, over **Tailscale** (`http://100.x.y.z:42617`
  or your `*.ts.net` name), a VPN, or a public URL. Plain `http` on a private network is fine; use `https` when the
  gateway is exposed beyond your trusted network.

## Install and connect

1. Download the latest `Kurobane-win-x64.zip` from the [releases](https://github.com/BookahCoding/Kurobane/releases),
   unzip anywhere your account can write, and run `Kurobane.exe`. No .NET installation is needed for the published zip.
2. Open **Settings…**, enter a name and your gateway address (e.g. `http://100.64.0.2:42617` over Tailscale).
3. Get a pairing code on the server (`zeroclaw gateway pair`, or the dashboard's pairing page), paste it into
   **Pairing code**, and press **Pair and test connection**.
4. Pick the **agent alias** the chats should run as (every ZeroClaw WebSocket session is bound to one agent), save,
   and start chatting.

## Security and privacy

- The bearer token is stored under `%APPDATA%\Kurobane\secrets`, encrypted with **Windows DPAPI** (current-user
  scope) — readable only by your Windows account on this PC.
- Non-secret settings (address, label, agent alias, appearance) live in `%APPDATA%\Kurobane\settings.json`.
- **Nothing is sent anywhere except your own gateway.** There is no telemetry and no account with anyone.
- Deleting a session asks first and cannot be undone; changing a gateway config property asks in words first.

## Build from source

You need Windows and the [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0). No other installs.

```powershell
./scripts/build.ps1    # build (warnings are errors), run the tests, publish to ./artifacts/Kurobane
./artifacts/Kurobane/Kurobane.exe
```

## Protocol notes

Kurobane speaks the published ZeroClaw gateway API:

- Auth: `POST /api/pair` with a pairing code → bearer token, sent as `Authorization: Bearer <token>` (on the
  WebSocket also as the `bearer.<token>` sub-protocol). All `/api/*` routes require it.
- Chat: `GET /ws/chat?agent_alias=…&session_id=…&name=…` (sub-protocol `zeroclaw.v1`). Send
  `{"type":"message","content":"…"}`; receive `session_start`, `connected`, `chunk`, `thinking`, `tool_call`,
  `tool_result`, `approval_request`, `done`, `aborted`, `error`. Answer approvals with
  `{"type":"approval_response","request_id":…,"decision":"approve|deny|always"}`.
- REST: `GET /api/status`, `GET /api/health`, `GET /api/sessions`, `GET /api/sessions/{id}/messages`,
  `DELETE /api/sessions/{id}`, `PUT /api/sessions/{id}` (rename), `POST /api/sessions/{id}/abort`,
  `GET /api/config`, `GET/PUT/DELETE /api/config/prop?path=…`.
