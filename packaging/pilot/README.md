# Portable Preset Sender pilot

Test machines receive prebuilt **self-contained executables**. They must not need
.NET/SDK, PowerShell, Git, Python, package restore or internet access. Private
runtime DLLs are application components and are never registered as a machine
runtime. Building and signing belong on the Windows development PC and Mac
development VMs.

Build both ZIPs on a development machine with Python, .NET 10 and PowerShell 7.2+:

```text
python packaging/pilot/build_portable.py --output OUTPUT_DIRECTORY --work NEW_BUILD_DIRECTORY
```

For centrally controlled runs, add `--connection PATH_TO_DASHBOARD_CONNECTION_JSON`.
The public connection file comes from the separate `crossplatform-testing`
dashboard. Updated bundles include Pair-with-Dashboard and Start-Agent launchers;
standalone Setup/Run still work. The reusable agent is pinned as a private NuGet
package in `dependencies/test-lab`. See `docs/test-lab-dashboard.md`.

When a Windows target is selected, the builder automatically prepares the
checksum-verified private MIDI build feed before publishing. This supports fresh
checkouts without installing a MIDI runtime. Mac-only builds skip this step and
do not require PowerShell. Test machines still need none of these build tools.

The builder exports the actual working tree identity, including uncommitted
changes, and publishes the application, existing tests, MIDI probe and portable
coordinator for Windows x64 and Apple Silicon macOS. It never stages, commits,
resets or distributes production executables. It writes hashes for every shipped
file and the final ZIP. PowerShell/dotnet host executables are explicitly rejected
from test bundles. No tool downloader/bootstrap is included.

`PresetSender.PortableTests` compiles the same existing test sources and uses
the matching xUnit 2.9.3 runner utility. This preserves the existing test behavior
and internal access contract without deploying vstest or a .NET CLI. The normal
development test project remains unchanged. The probe launches its own apphost
when published, rather than invoking `dotnet` for its independent MIDI receiver.

The Windows ZIP's `Setup-Windows.cmd` runs integrity checks, port discovery and
functional tests. `Run-Windows.cmd` requests functional, native and real desktop
checks. The Mac launchers request functional and native loopback checks; there is
no Mac desktop adapter yet. Missing native coverage gives exit 2 and an incomplete
report. There are no hardware sends, installation jobs or background services.

Windows legacy MIDI needs a manually configured loopMIDI test port. macOS uses
an IAC test bus. Both are called `PresetSender-Test`; the coordinator recognizes
the optional `IAC Driver ` display-name prefix. Windows MIDI Services tests use
the app's own virtual MIDI publication, and desktop tests use a unique test name.

The Mac ZIP is cross-built on Windows. Before transfer, run the included
`Prepare-on-Mac-DEV.command` on the Apple Silicon Mac development VM. It codesigns
Mach-O files, preserves the JIT entitlement required by the .NET apphosts,
verifies signatures and updates manifest hashes through the
self-contained coordinator's development-only `seal` mode. No SDK or PowerShell
is installed. Default ad hoc signing is for private lab copies; it is not
Developer ID notarization. Validate on the Mac development VM before copying
the whole prepared folder to the native boots/test Macs. The entitlement follows
[Microsoft's macOS publishing guidance](https://learn.microsoft.com/en-us/dotnet/core/deploying/macos). Test machines only use
Setup/Run. `PILOT_SIGNING_IDENTITY` optionally chooses an existing identity.

Each machine writes independent results under `results/`, with a latest-report
pointer, source identity, exact binary hashes, per-test JSON, logs, MIDI traces and
screenshots. For headless automation set `PRESET_SENDER_NO_PAUSE=1` and pass
`--no-open`; the launchers preserve the coordinator's exit code. Keep an unlocked
desktop available for Windows UI tests.
