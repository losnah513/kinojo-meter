# KINOJO Meter Desktop


기준일: 2026-08-04
Desktop version source: `release/version.json`  
Latest Meter SQL: `50015`
Desktop API Contract: `50015`
Edge API: `50015.2`

## KINOJO Meter 개발 분기

- 분기 기준: 2026-08-04 · Desktop `0.2.30`
- 이 지점 이후 Meter 원인 분석·구현·픽스처/실게임 검증·릴리스 상태는 WEB·일반 Server 작업과 분리해 기록합니다.
- 회차 마감은 GitHub / Server / Google Drive / 작업 로그를 각각 독립 상태로 보고합니다.
- 운영 Server 제출은 실제 게임에서 피해 합계 완전성과 보스·참가자 canonical 판정이 확인될 때까지 차단합니다.

## 0.2.35 모집 중 2~3인 파티 실시간 명단 판독

- 기존 파티 파서는 연속 구조화 레코드가 4명 이상일 때만 명단 이벤트를 만들었기 때문에 게임 파티창이 `2/5` 또는 `3/5`인 모집 단계에서는 본인 외 인원이 미터기에 나타날 수 없었습니다.
- 2~3인 명단은 AION2 `0x3641` envelope, PASS KEY 소유 캐릭터 이름 포함, 같은 명단 2회 연속 확인을 모두 통과할 때만 채택합니다. 4~6인 명단의 기존 즉시 판독은 유지합니다.
- TCP 꼬리 재검색으로 같은 명단의 레코드 시작 위치가 회전해도 확인 횟수가 초기화되지 않도록 명단 서명을 이름 기준으로 정규화합니다.
- Windows OCR은 패킷 명단을 먼저 받아야만 이름을 찾던 순환 의존을 제거했습니다. 현재 게임 창 캐릭터와 `캐릭터명[서버약칭]` 행을 두 번 연속 확인하면 임시 파티 명단으로 보강하고, 이후 패킷의 classRaw/serverRaw가 들어오면 같은 이름 행을 갱신합니다.
- 부분 명단은 기존 파티원을 즉시 제거하지 않으며, 파티원이 추가되면 이름 기준으로 실시간 upsert합니다. 프로필 조회와 클래스·서버·전투력 보강은 명단 행이 만들어진 직후 기존 Server 경로로 이어집니다.
- Decoder는 계속 `BINARY_PARTIAL_VALIDATED`, `UploadEligible=false`이며 공개 통계 Gate와 Edge/DB 계약은 변경하지 않습니다.

## 0.2.34 가변 피해 레코드·정확 1회 처리·프로필 서버 단서·저장 재시도

- 피해 레코드 첫 바이트를 고정 opcode로 보던 오류를 제거하고 레코드 길이로 해석합니다. 확인된 피해 효과 플래그 `06/16/26/36 + 00/04`를 읽되 피해량 값 자체는 제한 목록과 비교하지 않습니다. `739,455`, `133,825`, `127,368`은 회귀검사 기대값일 뿐 런타임 허용값이 아닙니다.
- 이전 4KB 재검색을 보정하던 30초 필드 중복 제거를 폐기했습니다. 같은 수치·스킬의 정상 연타는 모두 누적하고, TCP sequence 재전송과 raw/LZ4 이중 표현만 정확히 한 번 처리합니다.
- 최신 `fixture-20260804-143033` 재생에서 5명 명단을 유지하고, 1보스 피해는 기존 약 1,115만에서 약 1억 931만으로 복구했습니다. 2·3보스도 5명 공격자를 분리하지만 전체 피해 완전성은 아직 검증 중이므로 공개 통계 Gate는 닫아 둡니다.
- `serverRaw`를 파티 행과 프로필 요청 끝까지 보존합니다. Server Edge는 관측된 `raw + 1024`를 PLAYNC 서버 조회 힌트로만 사용하고, 공식 동일 이름·동일 서버 확인 뒤에만 프로필을 저장합니다.
- 전투 제출 payload를 로컬 outbox에 먼저 고정하고 성공 상태까지 기록합니다. 동의 미완료·네트워크 실패 시 다음 로그인에서 동일 `sourceEventId`로 자동 재시도하며, 트레이에서 웹 미터기 동의/기록 페이지를 바로 열 수 있습니다.
- 피해 행은 클래스 색 지분 게이지, 가장 큰 누적 피해량, 바로 옆 지분율, 바 중앙 DPS, `이름[서버]`·클래스·전투력의 2줄 구조로 재배치했습니다. `BRAWLER`는 기존 `fighter` 클래스 아이콘 자산에 연결합니다.

## 0.2.33 실제 관측 저장·파티원 공식 프로필 보강·WEB 최근 수집 기록

- 기존에는 `UploadEligible=false`인 순간 `UploadEncounterAsync`가 반환되어 로컬 outbox만 남고 Server에는 한 건도 저장되지 않았습니다. 이제 실제 NPCAP/WinDivert 캡처이고 파티 판독 피해가 있으면 `submitObservedEncounter`로 Server 격리 관측 저장소에 전송합니다.
- 부분 Decoder 결과는 `statistics_eligible=false`, `visibility=OWNER`로 고정합니다. 공개 통계·랭킹 제출 Gate는 계속 `BINARY_VALIDATED + SERVER_CANONICAL`일 때만 열립니다.
- 버스 파티에서 선택 캐릭터가 1·2보스를 공격하지 않아도 파티 총 판독 피해가 있으면 저장합니다. 보스 순서·런타임/구간 ID·관측 최대 HP·던전/난이도 단서·파티원별 피해/DPS/지분을 함께 보냅니다.
- 파티원 공개 프로필은 이름만으로 `meter_character_master`에 행을 만들지 않습니다. 공식 고유값, 서버 ID 또는 유일한 서버명 접두 단서가 있을 때만 기존 Master에 연결하고 모든 외부 조회 시도·결과를 별도 Server 이력으로 남깁니다.
- PLAYNC 검색 결과의 `<strong>` 이름 마크업을 제거하고, URL 인코딩된 공식 `characterId`를 한 번만 해제합니다. 상세 조회에는 필수 `serverId`를 전달하며 `/api/character/info`에서 클래스·전투력·아이템 레벨·프로필 이미지·안정 `charKey`를 읽습니다.
- 프로필 미해결 재시도 간격을 25초로 줄이고 성공 시 서버·클래스·전투력을 진단 로그에 남겨 오버레이 반영 여부를 확인할 수 있게 했습니다.
- 키노조 웹 미터기 페이지는 PASS KEY Meter 세션으로 `recentObserved`를 호출해 본인 계정의 최근 검증 전 수집 기록과 파티원별 판독 피해·공식 프로필 해결 상태를 표시합니다.

## 0.2.32 게임 창 제목 기반 즉시 캐릭터 자동 선택

- AION2 실행 창 제목에 표시되는 `AION2 l 캐릭터명`을 로그인 계정의 소유 캐릭터 목록과 정확히 교차검증해 자동 선택의 1순위 근거로 사용합니다.
- 0.5초 간격으로 같은 캐릭터를 2회 확인한 뒤 연결하므로 파티 명단 패킷이나 중앙 이름표 OCR을 기다리지 않습니다. 마우스·카메라·화면 중앙 고정도 요구하지 않습니다.
- 게임이 전면 창이 아니어도 창 제목 판독은 계속합니다. Windows OCR은 던전·난이도·파티 UI 보강용 1.5초 보조 경로로 유지합니다.
- 선택 이후에도 게임 창 제목을 감시해 같은 계정의 다른 캐릭터로 전환하면 Server 캐릭터 선택과 미터기 오버레이를 다시 연결합니다.
- 5초 안에 자동 선택되지 않으면 검색을 중단하지 않은 채 계정 캐릭터 카드를 자동으로 펼치고 `직접 선택` 대체 경로를 즉시 제공합니다.
- 진단 로그는 게임 창 대기, 제목 스캔, 소유 캐릭터 재확인, 확정, 5초 지연을 구분해 남깁니다. 파티 명단 패킷과 OCR은 보조 판정 근거로 계속 유지합니다.

## 0.2.31 버스 파티·다중 페이즈 보스·결과 고정 테스트

- `fixture-20260804-110906` 두 회차에서 런타임 대상 ID가 회차마다 바뀌고, 마지막 보스 `나트하라` 전투 안에서도 대상 ID `17039 → 19197`이 나뉘는 것을 확인했습니다. 런타임 ID를 보스 고유 ID로 저장하지 않고 던전 회차·조우 순서에만 한정합니다.
- 마지막 피해 후 12초 공백은 더 이상 보스 처치로 확정하지 않습니다. `PHASE_IDLE_12S`로 피해를 유지하고, 다음 대상이 같은 최종 보스 순서이면 같은 전투의 다음 페이즈로 합산합니다. 다음 보스 신호가 확인될 때만 앞 보스를 `NEXT_BOSS_SIGNAL`로 종료합니다.
- 처치 결과의 종료 시각·DPS·피해 지분을 즉시 고정합니다. 이후 파티 명단, HUD 던전/난이도, 공개 프로필 정보가 갱신돼도 끝난 보스 결과가 변하지 않습니다.
- 반복되는 4인 부분 명단은 5번째 0딜 대기 인원을 제거하지 않습니다. 동일 인원수의 완전한 교체 명단이 확인될 때만 이탈/교체를 반영해 버스 파티의 대기 인원을 보존합니다.
- HUD에서 판독한 던전명·난이도를 전투 엔진과 로컬 결과에 연결하고, 파티명 `캐릭터[서버]` OCR은 Server 공개 프로필 보강 힌트로만 사용합니다. 파티장 왕관을 자기 캐릭터 판정 근거로 사용하지 않습니다.
- 자동 캐릭터 판독은 중앙의 좁은 원본 크기 ROI와 넓은 자세 허용 ROI를 함께 읽고, 파티창에서는 소유 캐릭터 이름이 정확히 하나만 보일 때만 선택합니다.
- 전투 종료 결과를 `%LOCALAPPDATA%/KINOJO Meter/logs/outbox`에 `LOCAL_STAGED` JSON으로 가상 처리한 뒤 `다음 보스 전투 데이터 수집 대기` 상태로 전환합니다. 미검증 Decoder 결과는 Server로 전송하지 않습니다.
- 현재 피해 Decoder는 일부 피해 signature만 확인됐습니다. 실게임에서 다른 공격자가 함께 공격해도 한 명만 판독될 수 있으므로 UI에 `부분 피해/DPS`, `% 판독`으로 표시하고 실제 보스 딜 지분으로 단정하지 않습니다.
- Decoder·전투 엔진 회귀검사는 버스 대기 인원 보존, 다중 페이즈 누적, 명시적 종료, 결과 고정, HUD 메타데이터 연결 항목을 포함합니다.

## 0.2.30 런타임 보스 HP·순서 매핑·안정 파티 명단 테스트

- 최신 던전 픽스처에서 추가 확인된 자기 캐릭터 `0x33 0x36`, 다른 캐릭터 `0x45 0x36` 엔티티 이름 레코드를 지원합니다. 기존 `0x41 0x36`과 함께 런타임 ID를 이름에 연결해 파티원별 피해를 집계합니다.
- `FF FF` LZ4 봉투를 TCP 조각 크기와 무관하게 길이 기준으로 재조립합니다. 기존 512바이트 꼬리만 보던 구조 때문에 긴 봉투의 피해 이벤트가 유실되던 원인을 제거했습니다.
- 피해 대상에서 확인된 `0x14 0x00 0x8D + 대상 ID + 02 01 00 + 64비트 현재 HP`를 보스 현재 HP로 판독합니다. 첫 관측값은 정확한 총 체력 확정값이 아니므로 `OBSERVED_CURRENT_MAX`로 별도 표기합니다.
- 테스트 버전에서 서버 카탈로그와 정확히 일치한 던전명이 먼저 확인되면, 서로 다른 런타임 보스 ID를 조우 순서대로 1·2·3보스에 연결합니다. 운영 canonical ID로 승격하거나 서버에 제출하지 않습니다.
- 파티 명단이 순간적으로 4명만 잘려 들어와도 기존 5명 명단을 즉시 삭제하지 않습니다. 축소 명단이 3회 반복 확인될 때만 이탈로 확정하고, 전투 피해가 있는 행은 해당 보스전이 끝날 때까지 보존합니다.
- 자동 캐릭터 검색 창은 화면 중앙을 덮는 카드 페이지 대신 기존 미터 위치의 작은 비활성 상태창으로 시작합니다. `직접 선택`을 눌렀을 때만 계정 캐릭터 카드를 펼칩니다.
- 전투 중 행은 누적 피해 순으로 갱신하며 순위가 바뀔 때 위아래 위치 이동 애니메이션을 적용합니다. 피해 지분의 분모는 보스 HP가 아니라 파티에서 실제로 판독한 총 피해 합계입니다.
- 처치된 보스 결과는 서버 제출 여부와 관계없이 `%LOCALAPPDATA%/KINOJO Meter/logs/encounters-YYYYMMDD.jsonl`에 보스 순서·런타임 ID·관측 HP·참가자 이름/서버/클래스/전투력/피해/DPS/지분을 저장합니다.
- HP 0 이벤트가 확인되면 `HP_ZERO`, 마지막 피해 뒤 12초간 새 피해가 없으면 `DAMAGE_IDLE_12S`로 보스 구간을 종료합니다. 같은 런타임 보스 ID가 다음 회차에 다시 등장해도 이전 회차 피해를 초기화합니다.
- Decoder는 계속 `BINARY_PARTIAL_VALIDATED`, `UploadEligible=false`입니다. 최신 픽스처의 피해 합계가 관측 HP의 약 11~19%에 불과해 운영 Server 제출 Gate는 해제하지 않습니다.

## 0.2.29 피해/DPS 부분 검증·자동 검색 오버레이·관리자 진단 버튼

- 제어 픽스처로 확인한 `0x26 0x04 0x38` 피해 레코드를 로컬 미터기에 연결했습니다. 단타 `739,455`와 동일 액션의 연타 `133,825 + 127,368`을 각각 합산하며 TCP 분할·꼬리 중복은 제거합니다.
- `FF FF` 전송 봉투의 raw LZ4 블록을 해제하고, `0x41 0x36` 엔티티 이름과 런타임 ID를 연결합니다. 캐릭터 이름·피해 이벤트만으로도 이미 실행 중인 AION2 흐름에 합류할 수 있습니다.
- 패스키 인증 후 큰 수동 선택 페이지 대신 계정 캐릭터 카드와 KINOJO 링 스피너가 있는 자동 검색 오버레이를 띄웁니다. 감지된 카드 강조 후 미터기로 전환하며 카드 클릭은 수동 대체 경로입니다.
- 캐릭터 선택 뒤에도 HUD·파티 명단·엔티티 이름을 계속 비교해 다른 소유 캐릭터 접속을 감지하면 서버 선택과 미터기를 다시 연결합니다.
- 던전명과 난이도는 서버 카탈로그 정확 일치 OCR 결과만 표시하며, 피해 이벤트가 시작되면 보스 카탈로그 이름을 기준으로 전투 구간과 DPS를 자동 시작합니다. 12초간 피해가 없으면 해당 전투 구간을 종료합니다.
- 던전명만 인식된 상태는 `입장 대기`로 단정하지 않고 `던전 상태 확인 중`으로 표시합니다. 검증된 보스 피해가 들어오면 던전 입장과 보스 전투 상태로 전환합니다.
- 파티 행의 `1-1`, `#1` 표기를 제거하고 클래스 색 배지, `캐릭터명[서버]`, 클래스, 조회된 전투력을 표시합니다. 게이지는 해당 전투의 피해 지분을 클래스 색으로 채웁니다.
- 미터기 관리자에게만 오버레이의 `패킷 진단 수집 시작`/`수집 종료` 버튼을 표시하고 기존 트레이 메뉴와 동일한 수집기를 제어합니다.
- 디코더 상태는 `BINARY_PARTIAL_VALIDATED`입니다. 효과 플래그와 전체 전투 프로토콜은 아직 완전 검증되지 않았으므로 서버 제출은 계속 차단합니다.

## 0.2.28 게임 HUD 자동 판독·클래스 색상 지분 게이지

- PASS KEY 로그인 후 중앙 자기 캐릭터명 고정 영역을 Windows 한국어 OCR로 2회 연속 확인하면 사용자가 카드를 고르지 않아도 자동 연결합니다.
- 선택 이후에도 HUD 판독을 유지해 실제 접속 캐릭터가 바뀌면 Server `selectCharacter`와 오버레이를 다시 연결합니다.
- 화면 인식 결과는 로그인 계정 소유 캐릭터와 Server 던전 카탈로그에 정확히 일치할 때만 사용하며 임의 이름을 생성하지 않습니다.
- 파티 구성 창 상단의 던전명은 Server 카탈로그와 대조해 입장 전 표시 후보로 사용합니다. 실제 입장 상태는 검증된 `ZoneEntered`/`DungeonDetected` 패킷이 확인할 때만 전환합니다.
- 파티원 이름 왼쪽 클래스 아이콘의 고채도 대표색을 읽어 같은 `class_raw`에 연결하고, 보스 데미지 지분 게이지를 게임 클래스 색으로 표시합니다.
- HUD는 AION2가 전면에 있을 때 1.5초 간격의 두 고정 영역만 읽으며 전체 화면·원본 이미지를 저장하거나 업로드하지 않습니다.
- Windows 한국어 OCR을 사용할 수 없거나 확신도가 부족하면 기존 패킷 자동 선택과 수동 카드 선택을 안전한 대체 경로로 유지합니다.
- Damage/DPS 바이너리 디코더와 Server 제출 Gate는 계속 미검증·차단 상태입니다.

## 0.2.27 실시간 파티 상태 UI·캐릭터 변경 재판정

- 이동바 아래에 `파티 구성원 체크 중` 상태와 KINOJO 회전 스피너를 표시합니다.
- 파티 명단이 바뀌면 이전 probe 명단을 교체하여 현재 구성원만 오버레이에 표시합니다.
- 로그인 계정 소유 캐릭터 중 다른 캐릭터가 파티 명단에서 유일하게 확인되면 서버 선택 캐릭터와 오버레이를 자동 전환합니다.
- 던전·전투 상태는 해당 `DungeonDetected`/`ZoneEntered`/전투 이벤트가 실제로 수신된 경우에만 전환합니다.
- 오버레이 최하단에는 실행 중 Assembly 기준 `KINOJO Meter v0.2.27`을 표시합니다.
- Damage/DPS 바이너리 디코더와 서버 제출 Gate는 기존과 같이 미검증·차단 상태입니다.

## 0.2.26 파티 자동 인식 보정

- 레벨 45·50처럼 서로 다른 레벨이 섞인 파티도 `0x3641` 파티 명단 후보로 인정합니다.
- 감지된 파티 명단을 오버레이의 파티 슬롯에 즉시 표시합니다.
- 명단 표시는 Damage/DPS 디코더와 분리되어 있으며, 검증되지 않은 전투 수치는 생성하거나 서버에 제출하지 않습니다.
- Decoder: `aion2-late-attach-party-roster-probe-3`


이 폴더는 KINOJO Meter Desktop의 소스, 프로젝트, 설치기, 빌드 스크립트, CI, Payload 계약을 모두 보관하는 단일 루트입니다. KINOJO 전체 통합본의 최상위에 Meter 프로젝트 파일을 두지 않습니다.


## 폴더 구조


```text
05_METER_DESKTOP/
├─ README.md
├─ KINOJO.Meter.sln
├─ PREPARE_GITHUB_RELEASE.cmd
├─ VERIFY_GITHUB_RELEASE.cmd
├─ .github/workflows/windows-build.yml
├─ assets/
├─ release/version.json
├─ build/payload/
├─ projects/
├─ scripts/
├─ setup/
├─ src/
└─ KinojoMeter.ServerBridge/
```


- 앱 프로젝트: `projects/KINOJO.Meter.Test/KINOJO.Meter.Test.csproj`
- 설치기 프로젝트: `projects/KINOJO.Meter.Setup/KINOJO.Meter.Setup.csproj`
- 앱 소스: `src/*.cs`
- 설치기 UI·진입점: `setup/SetupProgram.cs`
- 설치 트랜잭션·복구 엔진: `setup/SetupEngine.cs`
- 버전 원본: `release/version.json`
- Windows 빌드: `BUILD_WINDOWS_RELEASE.cmd`, `scripts/build-windows.ps1`
- GitHub Actions: `.github/workflows/windows-build.yml`
- Payload 계약: `build/payload/README.txt`, 빌드 시 자동 동기화되는 `build/payload/version.json`
- WinDivert 원본·검증: `build/payload/WinDivert.dll`, `WinDivert64.sys`, `third-party-checksums.txt`


`KinojoMeter.ServerBridge`는 초기 Server 연결 경계를 보존한 라이브러리입니다. 현재 앱 본체는 같은 계약을 `src/KinojoApiClient.cs`와 전투 제출 흐름에 직접 통합합니다.


## 버전 관리


- 사람이 직접 변경하는 버전 원본은 `release/version.json` 하나입니다.
- 공식 Windows 빌드는 이 JSON의 `version`, `fileVersion`, `channel`, DB·Edge 계약을 먼저 검증합니다.
- 빌드 시 앱 EXE, 설치기 EXE, 설치기 파일명, Payload 파일명, 설치 폴더 `version.json`, checksum 파일명을 자동 동기화합니다.
- 앱 화면·트레이·API 요청은 하드코딩 문자열이 아니라 실행 중인 EXE Assembly 버전을 사용합니다.
- 실행 시 설치 폴더 `version.json`과 EXE 파일 버전이 일치하는지 진단 로그로 검증합니다.
- `build/payload/version.json`은 편집 원본이 아니며 빌드 과정에서 `release/version.json`으로 덮어씁니다.


## 역할 분담


### KINOJO Meter Desktop


- Npcap 우선·WinDivert 대체 네트워크 캡처
- TCP 흐름 추적과 재조립
- 검증된 AION2 바이너리 Decoder
- 파티·지역·던전·난이도·보스·피해 이벤트 감지
- 실시간 DPS·누적 피해·점유율 계산
- 트레이 백그라운드 실행과 게임 화면 연동 오버레이
- 전투 제출 Queue와 재시도


### Supabase Server Engine


- PASS KEY·사용자·캐릭터 소유권 검증
- Meter 전용 캐릭터 Master·Snapshot
- PLAYNC 공개 프로필 조회·6시간 캐시
- 카탈로그 canonical 정규화
- 전투 관계·중복·비정상 데이터 판정
- 전투 저장, 통계, 비교, 랭킹


### WEB


- 공개 통계와 내 전투 분석 표시
- 공통 로그인과 캐릭터 선택 연동
- Server가 확정한 결과만 표시


## 실행 흐름


1. `desktopBootstrap`으로 Server Catalog와 API·DB 계약을 확인합니다.
2. PASS KEY 로그인 후 패킷 파티 명단과 중앙 자기 이름 HUD를 함께 확인해 현재 캐릭터를 자동 선택합니다. 자동 확인이 불가능한 경우에만 수동 카드를 사용합니다.
3. 선택 즉시 트레이 백그라운드 실행과 네트워크 캡처를 시작합니다.
4. Npcap을 우선 사용하고 실패하면 WinDivert로 전환합니다.
5. TCP 흐름을 재조립해 AION2 바이너리 Decoder에 전달합니다.
6. 파티·지역·던전·난이도·보스·피해 이벤트를 자동 생성합니다.
7. 감지 문자열은 `resolveEncounterCatalog`로 보내 Server canonical key를 확정합니다.
8. 실제 캡처·검증된 Decoder·Server canonical 조건을 모두 충족한 전투만 `submitEncounter`에 전달합니다.
9. Server가 Meter 캐릭터 Master·Snapshot·중복·비정상·통계 포함 여부를 최종 판정합니다.


사용자의 기본 직접 조작은 `6자리 PASS KEY 입력`까지입니다. 프로그램이 현재 캐릭터를 확정하지 못한 경우에만 캐릭터 카드를 수동 대체 경로로 제공합니다. 콘텐츠·던전·난이도·보스를 사용자가 직접 선택하지 않습니다. 캐릭터 확정 후에는 트레이 전환, 캡처 엔진 선택, 캡처 재시도, 전투 감지, 오버레이 표시를 자동 처리합니다.




## 사용자 UX와 관리자 진단


- 로그인 화면은 KINOJO WEB PASS KEY 모달과 같은 6칸 입력 구조를 사용합니다.
- 캐릭터 카드는 HUD·패킷 자동 확인이 실패할 때 사용하는 수동 대체 경로이며 PURPLE 런처형 레이아웃과 본캐 우선 정렬을 유지합니다.
- 기본 오버레이는 던전·보스명, 경과 시간, 순위, 캐릭터, DPS, 누적 피해, 점유율과 클래스 색상 지분 게이지를 표시합니다.
- 일반 트레이 메뉴는 오버레이 표시·숨김, 로그아웃, 종료만 제공합니다.
- Server가 확인한 `meterAdmin`, `roleLevel`, `diagnosticsAllowed`를 우선 사용하며, 과도기 호환을 위해 Master 역할만 관리자 진단 메뉴로 인정합니다.
- 관리자 트레이 메뉴에서만 캡처 상태, 캡처 재시작, 진단 로그, 업데이트 확인을 제공합니다.
- 자동 오버레이 표시에서는 `Activate()`를 호출하지 않고 트레이 메뉴가 열린 동안 표시·숨김 타이머를 멈춰 메뉴 포커스가 사라지는 문제를 막습니다.
- 캡처 실패는 5초, 15초, 30초, 60초 간격으로 자동 재시도하며 일반 화면에는 `게임 연결 준비 중`만 표시합니다.
- 진단 로그는 `%LOCALAPPDATA%\KINOJO Meter\logs`에 저장하고 PASS KEY·세션 토큰은 기록하지 않습니다.


## 설치·업데이트


- 버전별 전체 설치기 하나가 신규 설치, 업데이트, 같은 버전 복구 설치를 자동 판정합니다.
- 기존 설치가 없으면 기본 `C:\Program Files\KINOJO Meter`에 신규 설치하며 설치 화면에서 경로를 변경할 수 있습니다.
- 기존 설치가 있으면 설치 경로를 유지하고 사용자가 만든 바탕화면·시작 메뉴 바로가기 상태도 유지합니다.
- 업데이트·복구 시 새 Payload를 임시 폴더에서 먼저 검증하고 기존 설치 폴더를 백업한 뒤 전체 프로그램 파일을 교체합니다.
- 설치 후 EXE 실행이 5초 이상 유지되는지 확인하며, 실패하면 기존 파일·바로가기·제거 프로그램 정보를 자동 복원합니다.
- 설치마다 `install-manifest.json`을 생성해 관리 파일의 크기와 SHA-256을 기록하고 복구 설치 검증에 사용합니다.
- 사용자 설정·오버레이 위치·로그는 `%LOCALAPPDATA%\KINOJO Meter`에 두므로 프로그램 파일 교체와 제거 후에도 유지합니다.
- 앱과 설치기는 관리자 권한을 자동 요청해 WinDivert 드라이버 권한을 확보합니다.
- Windows 제거 프로그램에는 제거와 복구 설치 진입점을 등록합니다.
- 기존 `%LOCALAPPDATA%\Programs\KINOJO Meter Test` 설치와 바로가기·레지스트리는 새 버전 실행 확인 후 정리합니다.
- 프로그램 시작 시 로그인 전 `desktopUpdate` action을 호출하고, 로그인 후 Catalog bootstrap에서도 같은 릴리스 계약을 다시 확인합니다.
- 매니페스트 필드: `version`, `fileVersion`, `minimumVersion`, `fileName`, `downloadUrl`, `sha256`, `fileSize`, `mandatory`, `releaseNote`, `publishedAt`, `channel`.
- Server에 활성 릴리스가 없으면 업데이트 영역을 표시하지 않고 기존 실행 흐름을 유지합니다.


## 캐릭터와 공개 프로필


- 기존 `character_master`는 Google `list`, 본캐·부캐, 권한, 성역, 레기온 랭킹용으로 유지합니다.
- Meter에서 실제로 만난 레기온·비레기온 캐릭터는 `meter_character_master`에 누적합니다.
- 아이온2 전체 캐릭터를 미리 수집하지 않습니다.
- 파티 구성은 패킷 이벤트로 먼저 확정하고 PLAYNC 공개 프로필은 Server 보완 정보로만 사용합니다.
- 현재 공개 프로필은 `meter_character_master`, 전투 당시 능력치는 `meter_character_snapshots`, 참가 기록은 `meter_participants`가 담당합니다.
- 서버 이전·이름 변경은 기존 `char_key`가 정확히 일치할 때만 기존 레기온 캐릭터 행을 갱신합니다.
- 프로필 조회 실패가 DPS 측정을 중단시키지 않습니다.


## 현재 운영 차단


`AionBinaryFrameDecoder`는 제어 픽스처의 단타·연타·일부 던전 피해와 런타임 HP 구조까지 확인한 `BINARY_PARTIAL_VALIDATED`입니다. 전체 피해 이벤트 완전성은 아직 검증되지 않았습니다.


관리자 패킷 진단 수집은 최대 20분·64MiB·100,000조각으로 제한됩니다. 시작 시 최근 최대 2분·8MiB 순환 버퍼를 먼저 기록하고, `frames.tsv`에는 익명 `connection_id`·방향·TCP sequence를, `markers.tsv`에는 관리자가 선택한 던전 진행 시점을 기록합니다. 원본 IP·포트와 PASS KEY·세션 토큰은 기록하지 않으며 자동 업로드하지 않습니다.


- Preview·JSON·추정 데이터 운영 제출 금지
- Desktop `UploadEligible=false`
- Payload `serverUploadEnabled=false`
- Server `kinojo_meter_submit_encounter_v4` Gate 활성


실제 Decoder 검증이 완료되기 전에는 이 차단을 해제하지 않습니다.

## Desktop 0.2.25 · 실행 중 연결 합류와 캐릭터 자동 선택

- PASS KEY 로그인 직후 캐릭터 선택 화면에서도 진단 캡처를 시작합니다.
- `0x3610/0x3611` 초기 교환을 놓친 기존 TCP 연결은 `0x3641 + varint` envelope와 4~6명의 연속 파티 레코드가 함께 확인된 경우에만 `LATE_ATTACH`로 승인합니다.
- 파티 레코드에서 PASS KEY 회원의 소유 캐릭터 이름이 정확히 한 명만 확인되면 해당 캐릭터를 Server `selectCharacter`에 자동 연결합니다.
- 같은 이름의 소유 캐릭터가 여러 서버에 있거나 소유 캐릭터가 둘 이상 동시에 후보가 되면 자동 선택하지 않고 기존 수동 선택 화면을 유지합니다.
- 자동 선택 전 탐지 캡처는 선택 확정 시 종료하고, 기존 Overlay 캡처로 안전하게 교체합니다.
- 구조화 파티 레코드는 자동 캐릭터 확인과 후속 파티 프로필 보강의 입력으로 전달합니다.
- 기존 fixture 3개 중 마커가 완전한 `fixture-20260723-232023`에서 `LATE_ATTACH`와 `청소기` 포함 4명 파티 구조화 이벤트를 재현했습니다.
- 피해 이벤트의 opcode·공격자·대상·피해량 필드는 아직 fixture로 확정되지 않았습니다. 추정 DPS를 생성하지 않으며 `UploadEligible=false`, `serverUploadEnabled=false`, Server 제출 Gate를 유지합니다.


## Windows 빌드


요구 환경:


- Windows 10/11 x64
- Visual Studio 2022 이상 또는 Visual Studio Build Tools
- .NET Framework 4.8 SDK·Targeting Pack
- MSBuild


KINOJO 통합본 루트에서 `05_METER_DESKTOP/BUILD_WINDOWS_RELEASE.cmd`를 실행하거나 PowerShell에서 다음을 실행합니다.


```powershell
cd 05_METER_DESKTOP
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\build-windows.ps1 -Configuration Release
```


컴파일 전 입력만 확인하려면 다음을 사용합니다.


```powershell
.\scripts\build-windows.ps1 -Configuration Release -PreflightOnly
```


빌드 스크립트는 다음을 자동 검증합니다.


- 앱·설치기 표준 매니페스트 `app.manifest`
- Drive 다운로드로 `app.manifest.xml`만 존재하면 XML 구조와 `requireAdministrator` 설정을 검증한 뒤 표준 이름으로 자동 복구
- `app.manifest`와 `app.manifest.xml`이 함께 존재할 때 내용이 다르면 안전을 위해 빌드 중단
- Visual Studio MSBuild와 .NET Framework 4.8 Targeting Pack
- WinDivert x64 DLL·드라이버의 고정 SHA-256
- NuGet 복원 후 `SharpPcap.dll`, `PacketDotNet.dll` 생성 여부
- 최종 Payload ZIP의 필수 항목
- Payload·설치기 SHA-256 기록


생성 결과:


```text
05_METER_DESKTOP\build\KinojoMeterPayload_<version>.zip
05_METER_DESKTOP\build\KINOJO_Meter_<version>_Setup.exe
05_METER_DESKTOP\build\checksums_<version>.txt
```


Payload에는 앱 EXE, NuGet 런타임 DLL, 검증된 WinDivert 파일, README, `version.json`, 제3자 바이너리 체크섬이 포함됩니다. Npcap은 Payload에 포함하지 않으며 설치되어 있지 않거나 장치를 열 수 없으면 WinDivert로 전환합니다.


`.github`는 이 폴더 안에 보관합니다. GitHub Actions를 실제 사용하려면 `05_METER_DESKTOP` 폴더 자체를 Meter 전용 저장소의 루트로 사용합니다. 전체 통합 저장소의 하위 폴더 상태에서는 GitHub가 이 Workflow를 자동 인식하지 않습니다.


## 외부 프로그램 안전 원칙


아이온2 운영정책에서 DPS 미터기, 패킷 캡처, 오버레이를 명시적으로 허용한다는 문구는 확인되지 않았습니다. 다음 기능은 구현하지 않습니다.


- 프로세스명 위장, 프로세스·창 은폐, 안티치트 탐지 우회
- DLL 인젝션, 게임 프로세스 핸들 접근, 메모리 읽기·쓰기
- 게임 파일·클라이언트 수정
- 자동 입력, 매크로, 자동 전투·자동 이동
- 캡처 드라이버 은폐
- 보안 프로그램 종료·방해·우회


일반 UX를 위한 `ToolWindow` 표시는 프로세스 은폐나 우회 용도로 사용하지 않습니다. 실제 게임 연동 배포 전에는 공식 허용 범위를 별도로 확인하고, 공식 전투 로그나 공식 API가 제공되면 이를 최우선으로 사용합니다.


## 보안·배포 전 확인


- Desktop에는 Supabase publishable key만 사용합니다.
- service role key, DB 비밀번호, PASS KEY를 파일이나 로그에 저장하지 않습니다.
- 로컬 별칭 목록이나 Server 기준정보 원본을 중복 저장하지 않습니다.
- Npcap·WinDivert 각각의 실제 Windows 캡처를 검증합니다.
- 게임 재접속·채널 이동·TCP 흐름 교체 시 재조립 초기화를 검증합니다.
- 트레이·오버레이·관리자 권한·드라이버 동작을 실제 Windows에서 확인합니다.
- Payload와 설치기의 SHA-256·크기를 기록합니다.
- 실제 배포본에는 코드서명과 업데이트 서명 검증을 적용합니다.


## 운영 반영 상태


- Supabase Meter SQL `50009~50015`: 운영 반영 완료
- `meter-ingest` Edge Function API `50015.2`: 운영 배포 완료
- Desktop 최신 소스 `0.2.34`, 마지막 Windows 실행 검증 `0.2.23`, Server 활성 stable 릴리스 `0.2.19`
- Damage/DPS Decoder 부분 검증으로 로컬 판독만 활성화하며 `UploadEligible=false`, `serverUploadEnabled=false`, Server 제출 Gate 유지
- `50009.sql`은 운영 스키마 기록 복구 파일이므로 재실행하지 않습니다.
- AppsScript_MASTER `BRIDGE.gs` 교체·재배포와 Extension 다시 로드는 별도 운영 반영이 필요합니다.


과거 단계별 상세 Meter 통합 문서와 이전 외부 프로그램 안전 문서 원본은 `99_LEGACY/KINOJO_LEGACY_SNAPSHOT_260723.zip`에 보존합니다.


## Windows PowerShell 5.1 encoding compatibility


`build-windows.ps1` is stored as UTF-8 with BOM and CRLF line endings, while all script literals remain ASCII-only. Do not add Korean or other non-ASCII text to this script. Windows PowerShell 5.1 may read a UTF-8 file without BOM as the system ANSI code page, which can corrupt quoted strings and produce a parser error such as `TerminatorExpectedAtEndOfString`.


If an older extracted folder prints broken text such as `鍮뚮뱶`, delete that extracted folder completely and extract the latest build-ready ZIP again. Do not overwrite only the ZIP while keeping the old extracted script.




## GitHub Release preparation and verification


The Desktop checks for updates before PASS KEY login through the public `desktopUpdate` action. A mandatory update blocks login and meter start until the verified installer is launched.


The update client accepts only a fixed GitHub Release URL in this form:


```text
https://github.com/<owner>/<repository>/releases/download/v<version>/KINOJO_Meter_<version>_Setup.exe
```


Before launching an update, the client verifies:


- HTTPS and the fixed `github.com` release path
- allowed GitHub redirect hosts only
- channel, semantic version, file version, and minimum version
- exact installer file name
- maximum size of 512 MB
- response size and downloaded byte count
- SHA-256
- installer Windows file version and product name


The installer then validates the embedded Payload `version.json`, application file version, required runtime files, and transactional rollback contract.


After a Windows build, prepare the GitHub Release metadata by running:


```text
PREPARE_GITHUB_RELEASE.cmd
```


Upload these two files to the generated `v<version>` GitHub Release:


```text
build\KINOJO_Meter_<version>_Setup.exe
build\checksums_<version>.txt
```


After upload, run:


```text
VERIFY_GITHUB_RELEASE.cmd
```


A successful remote verification creates:


```text
build\release\KINOJO_Meter_<version>_release-registration.json
```


This registration JSON is the input used to register the release in the Supabase Server Master. Do not activate a release when `remoteVerified` is false.


Code signing remains disabled for the internal update test. It must be enabled and verified before public distribution.


## Windows Sandbox clean-install validation


Use the isolated clean-install test after building a release installer:


```text
TEST_CLEAN_INSTALL_SANDBOX.cmd
```


The test does not modify the production KINOJO Meter installation on the host PC. It maps a temporary build test folder into a fresh Windows Sandbox, runs the same unified Setup EXE as a new installation, and verifies:


- release JSON, Setup file size, SHA-256 and file version
- clean install into `C:\Program Files\KINOJO Meter`
- required EXE, DLL, WinDivert and manifest files
- every managed file size and SHA-256 from `install-manifest.json`
- Windows uninstall registry entry and installed version
- desktop and Start menu shortcuts
- successful KINOJO Meter launch


Requirements:


- Windows 11 Pro, Enterprise or Education
- Windows Sandbox enabled in Windows Features
- the release installer already built in `build`


The host-side result is written to:


```text
build\sandbox-clean-install-<version>\results\clean-install-result.txt
```


Approve the elevation prompt inside Windows Sandbox if Windows displays one. After the automated report succeeds, visually confirm the PASS KEY screen and close the Sandbox window.
