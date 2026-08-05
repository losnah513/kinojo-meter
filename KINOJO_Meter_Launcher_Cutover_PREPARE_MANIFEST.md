# KINOJO Meter Launcher Cutover PREPARE

- Public repository: `losnah513/kinojo-meter`
- Archive base commit: `f4490c23d2fd79b3a7e9e2af160c9fd18f71b32f`
- Integration base commit: `9bde94b15960313df02cf110bf61217f31384cd9`
- Working branch: `agent/launcher-cutover-prepare`
- Target Launcher version: `1.0.0`
- Target private Core version: `0.2.38`
- Cutover state: `PREPARE_PRIVATE_PIPELINE`

This archive contains only the changed and newly added public-repository files required for the unsigned hobby Launcher installer, authenticated RSA-signed private-Core update manifest, integrity checks, ready handshake, and automatic rollback. It was prepared over the archive base and integrated after the runtime identity fix shown above. Do not activate WEB download or release rows until Windows CI, private repository publication, RSA contract verification, and clean-machine end-to-end validation pass.
