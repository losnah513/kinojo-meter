# KINOJO Meter Private Core repository template

신규 **private** Core 저장소에는 `src`, `assets/runtime`, `release/core-version.json`, `scripts/build-core-private.ps1`, `scripts/sign-core-release.ps1`과 실제 Core 구현만 둡니다. 현재 공개 저장소를 private으로 바꾸는 방식은 과거 공개 이력을 회수하지 못하므로 사용하지 않습니다.

필수 준비값:

- GitHub Environment `meter-core-production`. private 저장소에서 승인자 보호를 지원하지 않는 플랜이면 public 전환 대신 수동 `PUBLISH_CORE_<version>` 확인 Gate를 유지
- RSA-3072 private XML을 base64로 저장한 Environment Secret `KINOJO_CORE_SIGNING_PRIVATE_KEY_B64`
- 공개 계약 `RSA_SHA256_MANIFEST_V1`, key id `kinojo-core-rsa-2026-01`
- `KINOJO_CORE_SYNC_ENDPOINT`
- Server `meter-core-release-sync`의 private repository name/id/owner id/workflow ref 환경값
- `meter-core-private` private Storage bucket과 public policy 부재 확인
- `contracts/native-core-boundary.md`의 fixture 성능 Gate

RSA 개인키와 Supabase service-role key는 GitHub 저장소·artifact·로그·클라이언트에 넣지 않습니다. 공개키만 Launcher와 Server Edge에 고정합니다.

workflow 동작:

1. PR과 일반 main 변경은 unsigned Release 빌드·회귀검사까지만 확인합니다.
2. `ACTIVE`, `main` 수동 실행, 정확한 `PUBLISH_CORE_<version>` 확인 문자열을 모두 충족한 실행만 Environment로 진입합니다.
3. 미서명 Core ZIP의 package SHA-256과 install-manifest SHA-256을 RSA-3072로 서명하고 self-verification합니다. WinDivert SYS의 기존 공급사 서명은 별도로 확인합니다.
4. GitHub OIDC를 Server가 저장소 ID·owner ID·private visibility·main·workflow·commit 단위로 검증합니다.
5. Server가 불변 Storage object를 다시 내려받아 size/SHA-256을 확인한 뒤 active release로 바꿉니다.
6. 중단 후 재시도 시 기존 object가 같은 size/SHA-256일 때만 upload를 생략하고 finalize합니다. 기존 object를 덮어쓰지 않습니다.

실제 중요 로직은 .NET 난독화만으로 보호 완료로 판단하지 않습니다. 새 Decoder·판정표·DPS aggregation은 private native module 안에서 batch 처리하고 managed UI에는 50ms snapshot만 노출합니다. 이 작업이 끝나기 전에는 Core cutover state를 ACTIVE로 바꾸지 않습니다.
