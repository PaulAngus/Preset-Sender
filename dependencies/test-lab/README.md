# Application Test Lab agent

Pinned private NuGet dependency, produced by the separate `crossplatform-testing` repository. The self-contained `PresetSender.Lab` host embeds this library; test machines need no separate runtime or tooling installation. See `packages.sha256` for the package digest.

The reviewed agent source is recorded in [Test Lab commit c988bdc](https://github.com/PaulAngus/crossplatform-testing/commit/c988bdcb146fc00ac91af760f45ac5c6030b0464).

The host uses the exact NuGet range `[0.1.0]`. Source mapping resolves
`ApplicationTestLab.Agent` only from this checked-in feed, so a missing package
fails restoration instead of falling back to a public feed or another version.

Rebuild a new version in the Test Lab repository with `dotnet pack src/ApplicationTestLab.Agent -c Release -o artifacts/packages`, then copy the versioned package here and update the exact version in the Lab project. Sender builds do not require a sibling repository checkout. Preserve package-version immutability.

Pairing and agent launchers are part of the portable pilot ZIPs. Application-specific test logic, MIDI routing and reports remain in this repository. The dashboard server and reusable transport code live in the separate Test Lab repository.
