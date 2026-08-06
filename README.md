# Galgu Watch ⏱

작업·공부 시간을 재고 기록하는 **초경량 Windows 스탑워치**.
ZBrush · 3ds Max 같은 무거운 DCC 프로그램과 하루 종일 같이 떠 있어도 부담이 없도록 설계했습니다 — **상주 메모리 약 11MB**.

## 기능

- **원클릭 스탑워치** — 트레이 상주 + 항상 위 미니 오버레이 (클릭해도 작업 중인 앱의 포커스를 뺏지 않음)
- **월간 캘린더 히트맵** — 날짜별 작업시간이 색으로 쌓이고, 클릭하면 세션 타임라인
- **자동 스크린샷** — 측정 중에만 주기 캡처(WebP 압축), 수동 캡처, 보관 기한 자동 정리
- **마크다운 작업일지** — 날짜별 일기, 스크린샷 클릭 한 번으로 본문 삽입, 자동 템플릿·자동 저장
- **일일 목표 + 스트릭** — 달성한 날 ✓, 연속 달성 🔥 카운트
- **정직한 기록** — 자리비움·절전·화면잠금 시 자동 정지, 강제 종료돼도 기록 복구
- **공유** — 하루 일지를 이미지 카드/단일 HTML로 내보내기, 디스코드 웹훅으로 채널에 바로 업로드
- **Discord Rich Presence** — 측정 중이면 디스코드 프로필에 "작업 중 + 경과 시간" 실시간 표시
- 다크/화이트 테마

## 구조

C# WPF(.NET 9) + WebView2 하이브리드:
- 하루 종일 떠 있는 부분(타이머·오버레이·트레이·캡처)은 **네이티브 단일 프로세스**
- 캘린더·일지 같은 열람 화면만 열 때 WebView2를 만들고, 닫으면 완전히 해제
- 외부 서버 없음 — 모든 데이터는 `%LOCALAPPDATA%\GalguWatch`에 로컬 저장 (SQLite + WebP)

## 빌드

```
dotnet build GalguWatch/GalguWatch.csproj -c Release
```

배포본(자기완결, .NET 설치 불필요):

```
dotnet publish GalguWatch/GalguWatch.csproj -c Release -r win-x64 --self-contained true -o dist/GalguWatch
```

## 문서

기획·설계·로드맵 전체: [PLAN.md](PLAN.md)
