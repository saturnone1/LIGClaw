param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$targetFramework = "net10.0-windows10.0.17763.0"
$executable = Join-Path $repositoryRoot "src\LIGClaw.Desktop\bin\$Configuration\$targetFramework\LIGClaw.Desktop.exe"

if (-not (Test-Path -LiteralPath $executable)) {
    throw "Desktop 실행 파일이 없습니다. 먼저 프로젝트를 빌드해 주세요: $executable"
}
if (Get-Process -Name "LIGClaw.Desktop" -ErrorAction SilentlyContinue) {
    throw "UI smoke 전에는 실행 중인 LIGClaw를 종료해 주세요. 기존 사용자 프로세스는 자동으로 종료하지 않습니다."
}

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class LIGClawSmokeWindowApi
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();
}
"@

$process = $null
try {
    $process = Start-Process -FilePath $executable -WorkingDirectory $repositoryRoot -PassThru
    $deadline = (Get-Date).AddSeconds(15)
    do {
        Start-Sleep -Milliseconds 200
        $process.Refresh()
    } while ($process.MainWindowHandle -eq [IntPtr]::Zero -and -not $process.HasExited -and (Get-Date) -lt $deadline)

    if ($process.HasExited -or $process.MainWindowHandle -eq [IntPtr]::Zero) {
        throw "LIGClaw 메인 창이 제한 시간 안에 열리지 않았습니다."
    }

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $processCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $process.Id)
    $mainWindow = $root.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $processCondition) | Where-Object { $_.Current.Name -eq "LIGClaw" } | Select-Object -First 1
    if (-not $mainWindow) { throw "LIGClaw UI Automation 루트를 찾지 못했습니다." }

    function Find-ByName([string]$name) {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $name)
        return $mainWindow.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
    }

    function Invoke-Navigation([string]$buttonName, [string]$pageName) {
        $button = Find-ByName $buttonName
        if (-not $button -or $button.Current.ControlType -ne [System.Windows.Automation.ControlType]::Button) {
            throw "탐색 버튼을 찾지 못했습니다: $buttonName"
        }
        $button.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $page = $null
        $pageDeadline = (Get-Date).AddSeconds(8)
        do {
            Start-Sleep -Milliseconds 150
            $page = Find-ByName $pageName
        } while ((-not $page -or $page.Current.IsOffscreen) -and (Get-Date) -lt $pageDeadline)
        if (-not $page -or $page.Current.IsOffscreen) { throw "페이지가 표시되지 않았습니다: $pageName" }
    }

    $examplePrompt = Find-ByName "예시 명령: 시스템 상태 확인"
    if (-not $examplePrompt -or $examplePrompt.Current.IsOffscreen) {
        throw "Precision Workspace의 빈 대화 예시 명령이 보이지 않습니다."
    }
    $examplePrompt.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $requestInput = Find-ByName "요청 내용"
    $requestValue = $requestInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    # 첫 실행의 SQLite 마이그레이션과 Sidecar 초기화가 UI dispatcher 시간을 잠시 사용할 수 있다.
    # 제품 동작을 취소하지 않고 UI Automation 관찰 창만 넉넉히 둔다.
    $exampleDeadline = (Get-Date).AddSeconds(8)
    while ($requestValue.Current.Value -ne "내 시스템 리소스 상태를 확인해 줘" -and
           (Get-Date) -lt $exampleDeadline) {
        Start-Sleep -Milliseconds 100
    }
    if ($requestValue.Current.Value -ne "내 시스템 리소스 상태를 확인해 줘") {
        throw "예시 명령이 자동 실행 없이 요청 입력에 채워지지 않았습니다."
    }
    $requestValue.SetValue("")

    Invoke-Navigation "설정" "설정 페이지"
    $baseUrlInput = Find-ByName "모델 API Base URL"
    $baseUrlInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("not-a-url")
    $connectionTestButton = Find-ByName "모델 연결 테스트"
    $connectionTestButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300
    $baseUrlError = Find-ByName "http:// 또는 https://로 시작하는 주소를 입력해 주세요."
    if (-not $baseUrlError -or $baseUrlError.Current.IsOffscreen) {
        throw "설정의 Base URL 필드 오류가 입력 위치에 표시되지 않았습니다."
    }
    Invoke-Navigation "실행 활동" "실행 활동 페이지"
    if (-not (Find-ByName "실행 상태 필터")) { throw "실행 활동 상태 필터가 없습니다." }
    if (-not (Find-ByName "실행 활동 정렬")) { throw "실행 활동 정렬이 없습니다." }
    Invoke-Navigation "기억 관리" "기억 관리 페이지"
    if (-not (Find-ByName "기억 검색")) { throw "기억 검색 입력이 없습니다." }
    if (-not (Find-ByName "기억 정렬")) { throw "기억 정렬이 없습니다." }
    Invoke-Navigation "예약 관리" "예약 관리 페이지"
    if (-not (Find-ByName "완료·취소된 예약도 표시")) { throw "예약 상태 필터가 없습니다." }
    if (-not (Find-ByName "예약 정렬")) { throw "예약 정렬이 없습니다." }
    Invoke-Navigation "Agent 작업 관리" "Agent 작업 관리 페이지"
    if (-not (Find-ByName "저장된 Agent 작업 목록")) { throw "Agent 작업 목록이 없습니다." }
    if (-not (Find-ByName "새 Agent 작업")) { throw "Agent 작업 생성 동작이 없습니다." }

    $topLevelWindowCount = @($root.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $processCondition) | Where-Object {
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window
        }).Count
    if ($topLevelWindowCount -ne 1) {
        throw "관리 페이지 탐색 중 예상하지 않은 최상위 창이 열렸습니다: $topLevelWindowCount"
    }

    foreach ($navigationName in @("대화 홈", "새 대화 시작", "설정", "실행 활동", "기억 관리", "예약 관리", "Agent 작업 관리")) {
        $navigation = Find-ByName $navigationName
        if (-not $navigation.Current.IsKeyboardFocusable) {
            throw "키보드로 탐색할 수 없는 목적지가 있습니다: $navigationName"
        }
    }

    $homeButton = Find-ByName "대화 홈"
    [LIGClawSmokeWindowApi]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    $homeButton.SetFocus()
    $homeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500

    [LIGClawSmokeWindowApi]::SetWindowPos(
        $process.MainWindowHandle,
        [IntPtr]::Zero,
        100,
        100,
        760,
        500,
        0x0040) | Out-Null
    Start-Sleep -Milliseconds 500

    Invoke-Navigation "설정" "설정 페이지"
    foreach ($requiredControl in @("모델 API Base URL", "모델 연결 테스트", "저장")) {
        $element = Find-ByName $requiredControl
        if (-not $element -or $element.Current.IsOffscreen) {
            throw "760×500 설정 화면에서 핵심 컨트롤이 보이지 않습니다: $requiredControl"
        }
    }
    Invoke-Navigation "실행 활동" "실행 활동 페이지"
    foreach ($requiredControl in @("실행 상태 필터", "실행 활동 정렬", "최근 실행 목록")) {
        $element = Find-ByName $requiredControl
        if (-not $element -or $element.Current.IsOffscreen) {
            throw "760×500 실행 활동 화면에서 핵심 컨트롤이 보이지 않습니다: $requiredControl"
        }
    }
    Invoke-Navigation "기억 관리" "기억 관리 페이지"
    foreach ($requiredControl in @("기억 검색", "기억 정렬", "저장된 기억 목록")) {
        $element = Find-ByName $requiredControl
        if (-not $element -or $element.Current.IsOffscreen) {
            throw "760×500 기억 화면에서 핵심 컨트롤이 보이지 않습니다: $requiredControl"
        }
    }
    Invoke-Navigation "예약 관리" "예약 관리 페이지"
    foreach ($requiredControl in @("완료·취소된 예약도 표시", "예약 정렬", "저장된 예약 목록")) {
        $element = Find-ByName $requiredControl
        if (-not $element -or $element.Current.IsOffscreen) {
            throw "760×500 예약 화면에서 핵심 컨트롤이 보이지 않습니다: $requiredControl"
        }
    }
    Invoke-Navigation "Agent 작업 관리" "Agent 작업 관리 페이지"
    foreach ($requiredControl in @("새 Agent 작업", "저장된 Agent 작업 목록")) {
        $element = Find-ByName $requiredControl
        if (-not $element -or $element.Current.IsOffscreen) {
            throw "760×500 Agent 작업 화면에서 핵심 컨트롤이 보이지 않습니다: $requiredControl"
        }
    }

    $newConversationButton = Find-ByName "새 대화 시작"
    if (-not $newConversationButton -or $newConversationButton.Current.IsOffscreen) {
        throw "760×500에서 새 대화 시작 동작이 보이지 않습니다."
    }
    $newConversationButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 300
    if (-not (Find-ByName "새 대화를 시작할 준비가 됐어요.")) {
        throw "명시적인 새 대화 경계가 동작하지 않았습니다."
    }

    $homeButton = Find-ByName "대화 홈"
    [LIGClawSmokeWindowApi]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
    $homeButton.SetFocus()
    $homeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    Start-Sleep -Milliseconds 500

    foreach ($requiredControl in @("요청 내용", "시스템 상태 확인", "보내기")) {
        $element = Find-ByName $requiredControl
        if (-not $element -or $element.Current.IsOffscreen) {
            throw "760×500 compact hero layout에서 핵심 컨트롤이 보이지 않습니다: $requiredControl"
        }
    }

    $requestInput = Find-ByName "요청 내용"
    $focusResult = "not-observed-window-inactive"
    if ([LIGClawSmokeWindowApi]::GetForegroundWindow() -eq $process.MainWindowHandle) {
        $focusDeadline = (Get-Date).AddSeconds(3)
        while (-not $requestInput.Current.HasKeyboardFocus -and (Get-Date) -lt $focusDeadline) {
            Start-Sleep -Milliseconds 100
        }
        if (-not $requestInput.Current.HasKeyboardFocus) {
            $focused = [System.Windows.Automation.AutomationElement]::FocusedElement
            throw "홈 복귀 후 요청 입력으로 키보드 포커스가 돌아오지 않았습니다. 현재 포커스: $($focused.Current.Name)"
        }
        $focusResult = "passed"
    } elseif (-not $requestInput.Current.IsKeyboardFocusable) {
        throw "홈 복귀 후 요청 입력을 키보드로 포커스할 수 없습니다."
    }

    $recentErrors = @(Get-WinEvent -FilterHashtable @{
            LogName = "Application"
            Level = 2
            StartTime = (Get-Date).AddMinutes(-5)
        } -ErrorAction SilentlyContinue | Where-Object { $_.Message -match "LIGClaw\.Desktop" })
    if ($recentErrors.Count -ne 0) {
        throw "UI smoke 중 Windows Application 오류가 기록되었습니다: $($recentErrors.Count)"
    }

    [pscustomobject]@{
        ProcessId = $process.Id
        IntegratedPages = 5
        TopLevelWindows = $topLevelWindowCount
        CompactLayout = "760x500"
        InputFocusRestored = $focusResult
        FieldValidation = "passed"
        ManagementSorting = "passed"
        ConversationBoundary = "passed"
        KeyboardNavigation = "passed"
        PrecisionWorkspace = "passed"
        ApplicationErrors = 0
        Result = "passed"
    } | Format-List
}
finally {
    if ($process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -ErrorAction SilentlyContinue
        Wait-Process -Id $process.Id -Timeout 8 -ErrorAction SilentlyContinue
        $process.Refresh()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
            Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue
        }
    }
}
