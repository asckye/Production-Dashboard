# plc-sim · 软件 PLC / Software PLC

在没有硬件的情况下模拟 Siemens S7 PLC 的「完成信号」,用于本地开发、调试和演示。基于 [python-snap7](https://github.com/gijzelaerr/python-snap7) 的 S7 服务端。

Simulates a Siemens S7 PLC's "part complete" signal without hardware, for local development, debugging and demos. Built on the S7 server in [python-snap7](https://github.com/gijzelaerr/python-snap7).

## 用法 / Usage

```powershell
pip install -r tools/plc-sim/requirements.txt
python tools/plc-sim/plc_sim.py                    # Edge 模式 / Edge mode
python tools/plc-sim/plc_sim.py --mode handshake   # Handshake 模式 / Handshake mode
```

然后把大屏指向本机 / Then point the dashboard at the local machine:

```powershell
dotnet run --project src/ProductionDashboard -- --Plc:IpAddress=127.0.0.1 --Display:Fullscreen=false --Watchdog:Enabled=false
```

Handshake 模式下大屏还需加 `--Plc:SampleMode=Handshake` / In Handshake mode, also pass `--Plc:SampleMode=Handshake` to the dashboard.

## 参数 / Options

| 参数 / Option | 默认 / Default | 说明 / Description |
|---|---|---|
| `--mode` | `edge` | `edge`:完成位脉冲 / pulse the complete bit;`handshake`:序号 +1 并置位 / increment the sequence counter and set the bit |
| `--host` | `127.0.0.1` | 监听地址 / Listen address |
| `--db` | `100` | DB 块号 / Data block number |
| `--min-interval` / `--max-interval` | `1.5` / `4.0` | 两件之间的随机间隔(秒)/ Random gap between parts (seconds) |
| `--pulse-ms` | `350` | Edge 模式下完成位保持时间 / How long the bit stays high in Edge mode |

DB 布局与默认 `appsettings.json` 一致 / The DB layout matches the default `appsettings.json`:`DBX0.0` 完成位 / complete bit,`DBD2` 序号 / sequence counter,`DBW6` 心跳 / heartbeat.

> S7 默认端口为 102。仅监听 `127.0.0.1` 时不会暴露到网络 / S7 uses port 102. Listening on `127.0.0.1` only keeps it off the network.
