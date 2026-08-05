# KINOJO Meter Private Core repository template

이 폴더의 workflow를 신규 **private** Core 저장소의 `.github/workflows/core-private-release.yml`로 적용합니다. 현재 공개 저장소를 private으로 바꾸는 방식은 과거 공개 이력을 회수하지 못하므로 사용하지 않습니다. 신규 private 저장소에는 `src`, `assets/runtime`, `release/core-version.json`, `scripts/build-core-private.ps1`과 실제 native Core 구현만 둡니다.

필수 준비값:

- GitHub Environment `meter-core-production`의 승인자 보호
- Azure Artifact Signing 계정·인증서 프로필
- GitHub OIDC와 Azure federated credential
- `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`
- `ARTIFACT_SIGNING_ENDPOINT`, `ARTIFACT_SIGNING_ACCOUNT`, `ARTIFACT_SIGNING_PROFILE`
- `KINOJO_CORE_SYNC_ENDPOINT`
- Server `meter-core-release-sync`의 private repository name/id/owner id/workflow ref 환경값
- `meter-core-private` private Storage bucket과 public policy 부재 확인
- `contracts/native-core-boundary.md`의 fixture 성능 Gate

장기 코드서명 개인키와 Supabase service-role key는 GitHub 저장소에 넣지 않습니다.

workflow 동작:

1. PR과 일반 main 변경은 unsigned Release 빌드까지만 확인합니다.
2. `core-version.json`이 바뀐 ACTIVE main 또는 승인된 main 수동 재시도만 보호 Environment로 진입합니다.
3. Azure OIDC로 전체 EXE/DLL을 서명한 뒤 로컬에서 publisher를 다시 확인합니다.
4. GitHub OIDC를 Server가 저장소 ID·owner ID·private visibility·main·workflow·commit 단위로 검증합니다.
5. Server가 불변 Storage object를 다시 내려받아 size/SHA-256을 확인한 뒤 active release로 바꿉니다.
6. 중단 후 재시도 시 기존 object가 같은 size/SHA-256일 때만 upload를 생략하고 finalize합니다. 기존 object를 덮어쓰지 않습니다.

실제 중요 로직은 .NET 난독화만으로 보호 완료로 판단하지 않습니다. 새 Decoder·판정표·DPS aggregation은 private native module 안에서 batch 처리하고 managed UI에는 50ms snapshot만 노출합니다. 이 작업이 끝나기 전에는 Core cutover state를 ACTIVE로 바꾸지 않습니다.
