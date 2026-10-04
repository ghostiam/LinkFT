# SteamLink Module for VRCFT (Enhanced Fork)

An enhanced fork of [LinkFT](https://github.com/ykeara/LinkFT) with support for **custom face/eye tracking overrides** and full integration with **[Qpro-Enhanced-FT-Wireless](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless)**.

### Key Features
- **Face & Eye Tracking Overrides**: Flexible `IOverrider` architecture allowing interception and modification of any facial blendshape or eye parameter.
- **Qpro-Enhanced-FT-Wireless Support**: Native integration for enhanced tongue tracking, gaze, pupil diameter, and cheek calibration without breaking the default Steam Link behavior.

The Implementation is **EXPERIMENTAL** and may change without notice causing app to fail.

## Install

- Download Latest Release from [Releases](https://github.com/ykeara/LinkFT/releases/latest)
- In VRCFT go to modules and install from zip


## Setup

- **DO NOT CHANGE VRCFT NETWORK SETTINGS**

- In VR you will need to change a few settings in SteamLink, you will need to have "Advanced Settings" to show
  - Enable OSC - on
  - Share eye tracking data... - on
  - Share Face tracking data... - on
  - OSC Output Port - 9015 (Custom: VRCFT,Etc.)

## Face Tracking Overrides

A flexible override system (`IOverrider`) has been introduced to intercept, modify, and selectively override any face tracking (`UnifiedExpressions`) and eye tracking (`EyeExpression`: gaze coordinates, pupil diameter, eyelid positions) values:

- Preserves the stable and familiar behavior of the default module (full tracking for nose, lips, brows, and eyes).
- Allows hooking external filtering algorithms, calibrations, or neural network models on top of the standard Steam Link stream.
- Handles forwarding raw OSC messages (`/sl/xrfb/facew/`) for advanced processors.

## Qpro-Enhanced-FT-Wireless Support

Added full support and compatibility with [Qpro-Enhanced-FT-Wireless](https://github.com/Fwooffy/Qpro-Enhanced-FT-Wireless) (A Windows Hub for enhanced face tracking on a rooted Meta Quest Pro).

> **Why this was added:** The module bundled with `Qpro-Enhanced-FT-Wireless` modifies default behaviors and breaks parts of standard tracking (specifically, nose tracking / `NoseSneer` stops working). \
> This implementation maintains the classic behavior of the standard Steam Link module while providing all necessary communication protocols with the external runtime.

### Installation Instructions (Qpro-Enhanced-FT-Wireless Setup)

1. Compile or [download](https://github.com/ghostiam/LinkFT/releases) `Qpro.GazeBridge.dll` and `000-Qpro.SteamLink.dll` (these are identical files from the compiled module build).
2. Place `000-Qpro.SteamLink.dll` into the VRCFT custom libraries folder:
   ```
   %AppData%\VRCFaceTracking\CustomLibs\
   ```
   *(full path: `C:\Users\<Username>\AppData\Roaming\VRCFaceTracking\CustomLibs\`)*
3. Place `Qpro.GazeBridge.dll` into the runtime directory:
   ```
   QproFaceTracking\QproRuntime\vrcft-gaze-bridge\bin\Release\net10.0\
   ```
4. Restart **VRCFaceTracking (VRCFT)**.

## Mixed Tracking / 混合追踪

This module automatically cooperates with other VRCFT tracking modules to avoid overlapping data:

- When another module's **face tracking** is already active (initialized first), this module only claims **eye tracking**. VRCFT's main page will show "Eye Tracking Active" without "Expression Tracking Active", and this module will stop writing expression data.
- When another module's **eye tracking** is already active, this module only claims **expression tracking** (and vice versa).
- The claim is decided by the handshake with VRCFT during module initialization. Module load order (by module folder name) determines which module claims first.

### Manual override (tracking_config.json)

Because load order is alphabetical by module folder (effectively random), this module may load **before** another face tracking module and claim everything. In that case, create/edit the config file next to the module DLL:

`%APPDATA%\VRCFaceTracking\CustomLibs\2a8c8080-2a76-46af-bf76-1da7c0127ef8\tracking_config.json`

```json
{
  "EyeTracking": "Auto",
  "FaceTracking": "Off"
}
```

Values (case-insensitive):

| Value | Behavior |
|---|---|
| `Auto` | Follow VRCFT handshake: claim only what no other module has claimed (default) |
| `On` | Always claim this tracking type |
| `Off` | Never claim this tracking type (let other modules take it) |

Example: with another face tracking module installed, set `FaceTracking` to `Off` so SteamLink only provides eye tracking. Restart VRCFT after changing the config.

The config file is created automatically with defaults on first launch.

Note: toggling this module off on VRCFT's main page pauses its data output until toggled back on.

<img src="docs/steamlinksettings.png" width="600" alt="Root Page">
