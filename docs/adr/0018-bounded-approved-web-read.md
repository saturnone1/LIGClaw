# ADR 0018 — 승인 기반 bounded Web 읽기

## 결정

Protocol 1.8은 schema-first `web.fetch.v1`과 `web.search.v1`을 추가한다. Sidecar는 모델에 Tool 스키마만 제공하고 실제 HTTP 요청은 Desktop의 R3 승인·감사 파이프라인에서 실행한다. `web.fetch`는 사용자가 승인한 HTTP(S) URL에 GET만 보내며, `web.search`는 Desktop 설정에 저장된 URL 템플릿의 `{query}`만 URL 인코딩된 검색어로 치환한다.

Desktop HTTP 클라이언트는 쿠키를 저장하지 않고 자동 redirect를 끈다. 각 redirect URL을 다시 검증하며 최대 5회, 압축 해제 후 읽는 본문은 최대 512KB, 모델에 반환하는 텍스트는 최대 60,000자로 제한한다. `text/*`, JSON, XML, HTML만 허용하고 HTML의 script·style·태그를 제거한다. 최종 URL, status, content type, redirect 횟수와 truncation 여부를 결과에 포함한다.

URL은 HTTP(S) scheme, 길이와 포함된 사용자 정보만 검증한다. IP 주소 대역이나 hostname을 근거로 차단하지 않는다. 다운로드, 업로드, 인증 입력, 임의 header, POST와 쿠키 세션은 이 계약에 포함하지 않는다.

## 이유

- 에어갭·사내망 검색 서버와 사용자가 선택한 외부 endpoint를 같은 경계에서 사용할 수 있어야 한다.
- 모델이나 외부 콘텐츠가 네트워크 실행 권한과 요청 방식을 확장하지 못하게 한다.
- prompt injection을 신뢰 경계로 해결할 수는 없으므로 반환 콘텐츠를 명시적으로 untrusted data로 표시하고 크기·형식·동작을 제한한다.
- redirect와 압축 응답을 포함해 실제로 읽는 데이터 양을 결정적으로 제한한다.

## 결과

- 직접 URL 조회와 구성형 검색은 매 요청 R3 승인을 받으며 감사 이력에 남는다.
- 검색 공급자를 구성하지 않아도 직접 fetch는 사용할 수 있고, search만 안전하게 실패한다.
- endpoint가 공인망인지 사내망인지 Desktop이 추측하거나 차단하지 않는다.
- 별도 Edge 프로필과 접근성 snapshot 자동화는 후속 A-3.2에서 이 읽기 경계 위에 추가한다.
