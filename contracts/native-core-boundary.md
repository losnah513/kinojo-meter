# Native Core boundary v1

상태: 설계 고정 / 구현은 신규 private Core 저장소에서만 수행

목표는 Decoder·전투 판정·DPS 누적을 로컬 네이티브 worker 안에 유지하면서 업데이트·Server·UI가 실시간 경로를 막지 못하게 하는 것이다. 이 문서와 ABI 형태는 공개할 수 있지만 실제 opcode, 판정표, 키와 구현은 공개 저장소에 두지 않는다.

## 처리 경로

1. 캡처 callback은 고정 크기 SPSC ring buffer에 `{timestamp, flow, sequence, payload}`를 복사하고 반환한다.
2. 단일 native worker가 TCP 재조립, Decoder, 보스/참가자 판정, 누적 피해·DPS 계산을 순서대로 수행한다.
3. UI는 50ms마다 immutable snapshot을 조회한다. 이벤트별 managed/native callback은 사용하지 않는다.
4. ring buffer가 임계치에 도달하면 진단·로그·UI snapshot 생성을 먼저 생략한다. 전투 payload를 조용히 폐기하지 않으며 overload 상태를 명시적으로 표시한다.
5. 네트워크 요청, 파일 I/O, 코드서명 확인, 업데이트 확인과 telemetry는 native realtime worker에서 금지한다.

## ABI 원칙

- `kinojo_core_create(config, out_handle)` / `kinojo_core_destroy(handle)`
- `kinojo_core_push_batch(handle, frames, frame_count)`
- `kinojo_core_read_snapshot(handle, buffer, capacity, out_length, out_sequence)`
- `kinojo_core_status(handle, out_status)`
- 호출 규약과 구조체 packing을 버전으로 고정하고 모든 buffer는 호출자가 소유한다.
- snapshot은 schema version과 monotonic sequence를 포함한다.
- session token, PASS KEY, Storage URL은 ABI와 native 메모리에 전달하지 않는다.

## 성능 승인 기준

- fixture replay 입력 유실 0건, 순서 역전 0건.
- P99 enqueue 100µs 이하, decode+aggregate queue lag 5ms 이하.
- UI가 2초 멈춰도 누적 피해와 encounter clock 결과가 동일해야 한다.
- fixture writer와 일반 로그를 강제로 느리게 해도 DPS 결과가 동일해야 한다.
- 네트워크 단절 중에도 이미 실행된 Core의 로컬 전투 연산이 계속돼야 한다.

위 수치는 clean Windows VM과 실제 게임 fixture에서 측정한 결과로 승인한다. 측정 전에는 “무지연”으로 선언하지 않는다.

## 보호 승인 기준

- Release PDB와 map 파일은 배포 패키지에 포함하지 않는다.
- native module과 host EXE/DLL 전체가 동일 승인 게시자로 Authenticode 서명돼야 한다.
- package manifest에 없는 파일, 잘못된 publisher, size/SHA-256 불일치는 실행 전에 차단한다.
- 중요 문자열과 판정표는 평문 resource로 두지 않는다. 암호화 키를 바이너리에 고정해 완전 보호된다고 주장하지 않는다.
- public repository에는 native 구현·테스트 fixture·symbol·private workflow 산출물을 올리지 않는다.

네이티브화는 역분석 비용을 높이는 수단이며 절대적인 비가역 보호가 아니다. 서버로 연산을 옮기지 않는 이상 숙련된 분석자의 동적 분석 가능성은 남는다.
