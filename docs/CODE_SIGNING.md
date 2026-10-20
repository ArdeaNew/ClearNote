# Code signing policy

Status: the SignPath Foundation application was submitted on October 20, 2026 and is awaiting review. Production signing is unavailable. Existing releases are unsigned. This integration does not imply that a certificate has been issued or that any release is signed.

## Ownership and approval

Author, reviewer and release approver: [ArdeaNew](https://github.com/ArdeaNew). Source repository: [ArdeaNew/ClearNote](https://github.com/ArdeaNew/ClearNote).

The project has applied for free code signing provided by [SignPath.io](https://about.signpath.io), with a certificate provided by [SignPath Foundation](https://signpath.org). These services are subject to the Foundation's acceptance; no certificate has been granted to ClearNote yet.

After the Foundation accepts the project, configure a production signing policy with its approved certificate, manual approval, verified origin, and the GitHub.com trusted build system. Restrict the repository URL to this repository and the allowed branch to `main`. Use GitHub-hosted runners. Review build scripts together with application changes; origin verification cannot establish the safety of arbitrary downloaded code.

The CI identity must have only Submitter permission for this policy. Human maintainers approve signing requests in SignPath; the CI token must not have Approver or administrator permissions. Maintainers must use multi-factor authentication for both GitHub and SignPath. Use [signpath-artifact.xml](signpath-artifact.xml) as the artifact configuration. It signs only the ClearNote executable inside the uploaded ZIP.

## GitHub configuration

A repository administrator must create the `release-signing` environment, allow only the `main` branch, require a human reviewer, and disable protection-rule bypass. If this is a one-maintainer project, enabling prevention of self-review requires another reviewer who can approve runs.

Set these values in that environment, after production access is approved:

| Type | Name | Value |
| --- | --- | --- |
| Secret | `SIGNPATH_API_TOKEN` | Dedicated CI Submitter token |
| Variable | `SIGNPATH_ORGANIZATION_ID` | SignPath organization ID |
| Variable | `SIGNPATH_PROJECT_SLUG` | Approved project slug |
| Variable | `SIGNPATH_SIGNING_POLICY_SLUG` | Production policy slug |
| Variable | `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` | Configuration matching the XML above |
| Variable | `SIGNPATH_CERTIFICATE_SHA256` | Approved publisher certificate SHA256 fingerprint, 64 hexadecimal characters without separators |

Enter tokens directly into GitHub Secrets. Do not commit them, send them in issues, or provide private keys. Update the approved certificate fingerprint deliberately when the production certificate changes. Do not configure a test or self-signed certificate for this workflow.

## Signing a release

Manually run **Sign portable release** on `main`. It has no file, artifact ID, or source-ref inputs. Forks, pull requests, other branches and workflow reruns cannot use its signing jobs. Missing configuration causes an explicit failure; there is no unsigned fallback.

The workflow builds and runs the existing checks using `src/build.ps1`, then uploads only the resulting `ClearNote.exe`. The signing job receives that upload's immutable artifact ID, following the build dependency, and waits for environment approval before accessing the token. SignPath independently verifies the repository and build origin, then requires its own human approval.

The returned directory must contain exactly `ClearNote.exe`. Verification requires matching product/version metadata, a valid embedded Authenticode signature, the approved publisher certificate, code-signing EKU, a timestamp certificate with timestamping EKU, and successful Windows trust verification with `SignTool verify /pa /all /tw`. Missing timestamps, untrusted signatures and warnings fail the job. Provision production timestamping through SignPath; the workflow does not substitute another certificate or timestamp authority.

Only after verification does the existing packaging tool create a portable ZIP containing the signed executable, README and license. The workflow uploads this ZIP as a build artifact. It does not publish a GitHub release, change tags, or replace existing downloads. Before publishing, review the successful run, source commit, signing approval and packaged executable. A trusted signature identifies the publisher; it does not guarantee that SmartScreen will immediately stop displaying reputation warnings.

## Privacy

ClearNote works offline and does not send notes, images, profile information or usage data to networked services. It has no analytics or cloud account. Active data is stored locally in the `Log` folder beside the executable. The user chooses whether to export or share a backup.

The development signing workflow uploads only the build executable to SignPath. No runtime signing service is added to the application.

## Compatibility

The workflow uses `actions/upload-artifact@v7` with its default ZIP archive and `signpath/github-action-submit-signing-request@v3`. The returned artifact ID supplies GitHub origin verification. Keep ZIP mode paired with the `<zip-file>` configuration; do not enable `archive: false` or `skip-decompress`. The signing action uses its own default GitHub.com connector endpoint.

Configuration follows the [SignPath GitHub integration](https://docs.signpath.io/trusted-build-systems/github), [artifact configuration](https://docs.signpath.io/artifact-configuration/reference), [project policies](https://docs.signpath.io/projects), [origin verification](https://docs.signpath.io/origin-verification/), [GitHub environments](https://docs.github.com/en/actions/reference/workflows-and-actions/deployments-and-environments) and [Microsoft SignTool](https://learn.microsoft.com/en-us/windows/win32/seccrypto/signtool) documentation.
