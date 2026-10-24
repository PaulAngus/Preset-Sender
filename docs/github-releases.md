# GitHub application releases

Build the selected release commit from a clean checkout. Use PowerShell 7 and the
normal publish scripts; these are maintainer tools, not end-user requirements.
The Windows and Mac publish entry points use fresh short temporary intermediate
paths. Publish from a clean committed source tree. The Windows publisher records
the source tree, full source-file hashes, source bundle, exact MIDI package pins
and normal EXE/ZIP hashes. Existing versioned ZIPs are not replaced.

On Windows:

```powershell
./Publish-PresetSender.ps1
```

This produces `artifacts/windows/Preset-Sender-<version>-win-x64.zip` and its
`.zip.sha256`. The ZIP contains the standalone EXE and dependency notices.
Users extract the ZIP and run the EXE.

On the signing Mac, build each supported architecture:

```powershell
./Publish-Mac.ps1 -Architecture arm64 -SigningIdentity 'Developer ID Application: YOUR NAME (TEAMID)' -NotaryProfile 'YOUR-KEYCHAIN-PROFILE'
./Publish-Mac.ps1 -Architecture x64 -SigningIdentity 'Developer ID Application: YOUR NAME (TEAMID)' -NotaryProfile 'YOUR-KEYCHAIN-PROFILE'
```

The scripts print the exact ZIP paths under `artifacts/macos/`. Upload
`Preset-Sender-<version>-osx-arm64.zip` and `Preset-Sender-<version>-osx-x64.zip`,
with their `.zip.sha256` files. `ditto` preserves the complete `.app`, permissions
and symbolic links. Users extract the ZIP and copy the `.app` to Applications.
ZIP is the chosen Mac distribution format; a DMG is not required.

## Windows signing

On the Windows build PC, install the Windows SDK signing tools and Certum card
software, activate/register the approved certificate in `Cert:\CurrentUser\My`,
and connect the reader/card. These tools are not application-user dependencies.
List available certificates without exporting the private key:

```powershell
Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert |
    Select-Object Subject, Thumbprint, NotAfter, HasPrivateKey
$env:MBU_WINDOWS_SIGNING_THUMBPRINT = 'YOUR-EXACT-40-CHARACTER-THUMBPRINT'
./Publish-PresetSender.ps1 -Sign
```

The card software may display a PIN prompt. Enter the PIN there only; never put
it or a private key in scripts, environment variables, Git or chat. SignTool is
discovered on PATH or in the installed Windows SDK. Override its location with
`-SignToolPath 'C:/full/path/to/signtool.exe'` or `MBU_SIGNTOOL_PATH` if necessary.

The publisher signs the final EXE with SHA-256 and an RFC 3161 SHA-256 timestamp
(default `http://time.certum.pl`, configurable with `-TimestampServer`). It checks
SignTool verification, Windows trust, the exact signer and the presence of a
timestamp before ZIP/checksum creation.
Missing certificates, token/PIN problems, timestamp failures and verification
failures stop the script. Upload only the ZIPs/checksums from a successful run.

`-Sign` requires a configured certificate and fails before building if absent.
Supplying a thumbprint (argument or environment) also enables signing. Without
either, the script explicitly reports an unsigned development build. To make an
unsigned development build despite an existing environment setting, pass
`-SigningCertificateThumbprint ''` and omit `-Sign`.

Initial Certum card signing remains pending certificate approval and card
arrival. Once available, publish with `-Sign`, extract each new ZIP and check
`Get-AuthenticodeSignature` reports `Valid`, the expected signer and a nonempty
`TimeStamperCertificate`; verify the ZIP SHA-256 sidecar against `Get-FileHash`.
Then run the required application workflows on the available machines and record
clean Windows 10/11 coverage separately. Simulated signing tests do not qualify
the certificate, token, timestamp service or downloaded application behavior.

References: [Microsoft SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool),
[Certum signing instructions](https://files.certum.eu/documents/manual_en/Signing_with_the_use_of_jarsigner_tool_and_signtool.pdf).

## Upload

Create a draft GitHub Release for an existing tag pointing at the tested commit.
Attach the exact ZIPs and checksums from that build, not a recursive wildcard of
old release directories. For example, using the GitHub CLI after setting `$tag`
to the release tag and `$archive` to one exact ZIP path:

```powershell
gh release create $tag --repo PaulAngus/preset-forwarder --verify-tag --draft --title $tag --notes-file release-notes.txt
gh release upload $tag $archive "$archive.sha256" --repo PaulAngus/preset-forwarder
```

Create the draft once; repeat only the upload command for the other archives.
Do not use `--clobber` to silently replace a released artifact. Download and
extract the uploaded archives, verify their checksums, and complete the required
first-launch, settings and MIDI workflow checks before publishing the draft.
Record unavailable clean OS/architecture tests explicitly. GitHub's automatically
generated source archives are not application downloads.

## Credentials and qualification

ZIP creation needs no developer key. Public Mac downloads need a Developer ID
Application certificate with its private key and notarization credentials
configured locally in the signing Mac's keychain. Pass only the identity name
and keychain-profile name to these scripts. Never send or commit private keys.
The default ad hoc signature is for development testing; a successful ZIP build
alone does not establish Gatekeeper approval or clean-machine qualification.
Windows code signing uses a separate Windows signing certificate; an Apple key
does not sign the Windows EXE. Compression does not bypass OS security checks.

The release scripts do not upload automatically. GitHub authentication is needed
only on the maintainer machine doing the upload.

References: [GitHub release creation](https://cli.github.com/manual/gh_release_create),
[asset upload](https://cli.github.com/manual/gh_release_upload),
[Apple distribution](https://developer.apple.com/developer-id/).
