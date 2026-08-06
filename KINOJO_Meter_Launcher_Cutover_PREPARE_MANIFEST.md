# KINOJO Meter Launcher Cutover PREPARE

- Public repository: `losnah513/kinojo-meter`
- Archive base commit: `c8afe9ec8092c8e430bff810d94948558ce80914`
- Integration base commit: `c8afe9ec8092c8e430bff810d94948558ce80914`
- Working branch: `agent/unsigned-hobby-rsa`
- Review: public PR `#9`
- Target Launcher version: `1.1.0`
- Target private Core version: `0.2.39`
- Cutover state: `PREPARE_PRIVATE_PIPELINE`

This archive contains only the changed and newly added public-repository files required for the unsigned hobby Launcher installer, authenticated RSA-signed private-Core update manifest, integrity checks, ready handshake, and automatic rollback. It was prepared over the archive base and integrated after the runtime identity fix shown above. Do not activate WEB download or release rows until Windows CI, private repository publication, RSA contract verification, and clean-machine end-to-end validation pass.
