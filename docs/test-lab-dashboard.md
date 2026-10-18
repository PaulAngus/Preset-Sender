# Central dashboard adapter

The shared server and reusable agent belong to the separate [crossplatform-testing](https://github.com/PaulAngus/crossplatform-testing) repository. Preset Sender owns the existing application tests, MIDI probes, desktop adapter and portable packaging. The versioned `ApplicationTestLab.Agent` package is checked into `dependencies/test-lab`, so a Sender build does not need another checkout.

The `PresetSender.Lab` self-contained host exposes `pair` and `agent` using that package. The agent only invokes this same host's fixed `run` command and allowed suites. `--result-directory` places each dashboard job in `results/job-JOB_ID`. It reuses the existing functional, native and desktop test paths.

Build target ZIPs on a development machine:

```
python packaging/pilot/build_portable.py --output OUTPUT --work WORK --connection PATH_TO_DASHBOARD_CONNECTION_JSON
```

The optional connection file contains the development PC URL and public certificate fingerprint. It is replaceable deployment configuration, excluded from immutable bundle hashes. It contains no pairing code or credential. Mac development signing seals immutable binaries as before. Runtime pairing credentials, agent state and job spools stay in the OS user's local application data, never the distributable ZIP.

On each test machine, run its Pair-with-Dashboard launcher once with a one-time code, then Start-Agent. Keep the agent running in an unlocked desktop for UI tests. On the dashboard choose a suite and machine; reports collect automatically. Refer to the separate Test Lab README for firewall setup, certificate pinning, state backup and failure recovery. Standalone Setup/Run launchers continue working without a dashboard.

Current limits are unchanged: Mac desktop automation, physical MIDI device readback, installation/upgrade qualification and signing/notarization qualification are separate work. No SDK, PowerShell, Git, Python or separate .NET runtime install is needed on test machines. Self-contained runtime files remain private application components.

## Validation — 18 October 2026

The published adapter passes the Test Lab's 32 protocol and integration checks,
including an All run collected after server/agent restart without rerunning the
tests. It reuses all 81 Windows functional tests, three native MIDI tests and six
desktop scenarios. A fresh checkout with empty NuGet caches passes Windows All
and all 80 macOS-targeted headless tests; the Mac restore needs no Windows SDK
feed. Agent restore probes accept only the bundled exact version and reject a
missing package or a higher version, even when an alternate feed has a copy.

Both platform ZIPs pass checksum, manifest, private-runtime and launcher checks.
The signed Apple Silicon macOS 27.0.1 bundle also passes all 80 functional tests
on the development VM. Mac native MIDI remains incomplete until its dedicated
IAC test bus is configured. Superseded MIDI archives are removed from the bundled
feed; the complete, exactly pinned 0.3.6 family and the agent package remain
checked in. Local verification evidence is retained outside Git.
