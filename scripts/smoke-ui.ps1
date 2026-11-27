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
Add-Type -AssemblyName System.Windows.Forms
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

function Assert-CapabilitySmtpReviewContract {
    $reviewXamlPath = Join-Path $repositoryRoot "src\LIGClaw.Desktop\CapabilityReviewWindow.xaml"
    $reviewCodePath = Join-Path $repositoryRoot "src\LIGClaw.Desktop\CapabilityReviewWindow.xaml.cs"
    $reviewXaml = Get-Content -LiteralPath $reviewXamlPath -Raw -Encoding UTF8
    $reviewCode = Get-Content -LiteralPath $reviewCodePath -Raw -Encoding UTF8

    foreach ($requiredXaml in @(
            'Width="760" Height="650"',
            'MinWidth="620" MinHeight="500"',
            'HorizontalScrollBarVisibility="Disabled"',
            'AutomationProperties.Name="기능 연결 준비 상태 다시 확인"',
            'AutomationProperties.Name="기능 연결 선택 목록"',
            'AutomationProperties.Name="{Binding SelectorAutomationName, Mode=OneWay}"',
            'Content="연결 선택 저장"')) {
        if (-not $reviewXaml.Contains($requiredXaml)) {
            throw "Capability SMTP 검토 UI 계약이 없습니다: $requiredXaml"
        }
    }

    foreach ($requiredCode in @(
            'CapabilityConnectionKindPolicy.Smtp',
            'smtpProfileChoices',
            'LocalConnectionProfileChoice Create(SmtpConnectionProfile profile)',
            '저장된 메일 서버 로그인 정보가 없습니다.')) {
        if (-not $reviewCode.Contains($requiredCode)) {
            throw "Capability SMTP binding/readiness 구현 계약이 없습니다: $requiredCode"
        }
    }
}

function Assert-CapabilityBuilderNavigationContract {
    $mainXamlPath = Join-Path $repositoryRoot "src\LIGClaw.Desktop\MainWindow.xaml"
    $builderCodePath = Join-Path $repositoryRoot "src\LIGClaw.Desktop\FeatureBuilderPage.xaml.cs"
    $mainCodePath = Join-Path $repositoryRoot "src\LIGClaw.Desktop\MainWindow.xaml.cs"
    $mainXaml = Get-Content -LiteralPath $mainXamlPath -Raw -Encoding UTF8
    $builderCode = Get-Content -LiteralPath $builderCodePath -Raw -Encoding UTF8
    $mainCode = Get-Content -LiteralPath $mainCodePath -Raw -Encoding UTF8

    foreach ($requiredCode in @(
            'DraftReadyForReview?.Invoke(this, result.Draft.ExactVersion)',
            '_featureBuilderPage.DraftReadyForReview += FeatureBuilderPage_DraftReadyForReview',
            'await _capabilitiesPage.RefreshAsync(completionMessage, focusVersion)',
            'FeatureBuilderPolicy.ValidateRequirement(requirement)',
            'await OpenFeatureBuilderAsync(requirement)',
            'ApplyInitialRequirement(initialRequirement)')) {
        if (-not ($builderCode.Contains($requiredCode) -or $mainCode.Contains($requiredCode))) {
            throw "Feature Builder 검토 초안 탐색 계약이 없습니다: $requiredCode"
        }
    }

    foreach ($requiredXaml in @(
            'x:Name="ComposerActions"',
            'x:Name="MakeCapabilityButton"',
            'AutomationProperties.Name="이 요청을 기능으로 만들기"',
            '요청을 보내거나 기능 생성을 시작하지 않습니다')) {
        if (-not $mainXaml.Contains($requiredXaml)) {
            throw "Composer → Feature Builder handoff UI 계약이 없습니다: $requiredXaml"
        }
    }
}

$process = $null
try {
    # Review 창을 열기 위한 Capability import는 smoke에서 제품 상태를 변경한다. 대신 현재 소스와
    # 함께 빌드되는 접근성 이름, compact 경계와 typed SMTP readiness 계약을 먼저 고정한다.
    Assert-CapabilitySmtpReviewContract
    Assert-CapabilityBuilderNavigationContract

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

    function Wait-VisibleByName([string]$name, [string]$context) {
        $element = $null
        $elementDeadline = (Get-Date).AddSeconds(8)
        do {
            Start-Sleep -Milliseconds 100
            $element = Find-ByName $name
        } while ((-not $element -or $element.Current.IsOffscreen) -and (Get-Date) -lt $elementDeadline)
        if (-not $element -or $element.Current.IsOffscreen) {
            throw "$context 요소가 표시되지 않았습니다: $name"
        }
        return $element
    }

    function Wait-VisibleScrollableByName([string]$name, [string]$context) {
        $element = $null
        $elementDeadline = (Get-Date).AddSeconds(8)
        do {
            $element = Find-ByName $name
            if ($element -and $element.Current.IsOffscreen) {
                $scrollItem = $null
                if ($element.TryGetCurrentPattern(
                        [System.Windows.Automation.ScrollItemPattern]::Pattern,
                        [ref]$scrollItem)) {
                    $scrollItem.ScrollIntoView()
                }
            }
            Start-Sleep -Milliseconds 100
        } while ((-not $element -or $element.Current.IsOffscreen) -and (Get-Date) -lt $elementDeadline)
        if (-not $element -or $element.Current.IsOffscreen) {
            throw "$context 요소를 스크롤해 표시하지 못했습니다: $name"
        }
        return $element
    }

    function Find-ProcessElement([string]$name, $controlType) {
        $nameCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::NameProperty,
            $name)
        foreach ($processRoot in $root.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                $processCondition)) {
            foreach ($element in $processRoot.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $nameCondition)) {
                if ($element.Current.ControlType -eq $controlType) { return $element }
            }
        }
        return $null
    }

    function Wait-VisibleScrollableControlByName(
        [string]$name,
        $controlType,
        [string]$context) {
        $element = $null
        $elementDeadline = (Get-Date).AddSeconds(8)
        do {
            $element = Find-ProcessElement $name $controlType
            if ($element -and $element.Current.IsOffscreen) {
                $scrollItem = $null
                if ($element.TryGetCurrentPattern(
                        [System.Windows.Automation.ScrollItemPattern]::Pattern,
                        [ref]$scrollItem)) {
                    $scrollItem.ScrollIntoView()
                }
            }
            Start-Sleep -Milliseconds 100
        } while ((-not $element -or $element.Current.IsOffscreen) -and (Get-Date) -lt $elementDeadline)
        if (-not $element -or $element.Current.IsOffscreen) {
            throw "$context 컨트롤을 스크롤해 표시하지 못했습니다: $name"
        }
        return $element
    }

    function Select-ComboBoxItem($comboBox, [string]$itemName, [string]$context) {
        if ($comboBox.Current.ControlType -ne [System.Windows.Automation.ControlType]::ComboBox) {
            throw "$context 대상이 ComboBox가 아닙니다."
        }
        $expand = $comboBox.GetCurrentPattern(
            [System.Windows.Automation.ExpandCollapsePattern]::Pattern)
        $expand.Expand()
        $item = $null
        $itemDeadline = (Get-Date).AddSeconds(2)
        do {
            Start-Sleep -Milliseconds 100
            $item = Find-ProcessElement $itemName ([System.Windows.Automation.ControlType]::ListItem)
        } while (-not $item -and (Get-Date) -lt $itemDeadline)
        if ($item) {
            $item.GetCurrentPattern(
                [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
        }
        if ($expand.Current.ExpandCollapseState -ne
            [System.Windows.Automation.ExpandCollapseState]::Collapsed) {
            $expand.Collapse()
        }

        $selection = $comboBox.GetCurrentPattern(
            [System.Windows.Automation.SelectionPattern]::Pattern)
        $selectedItems = @($selection.Current.GetSelection())
        if (-not $item) {
            # DisplayMemberPath 기반 WPF ComboBoxItem은 popup UIA 이름이 비어 있을 수 있다.
            # SMTP는 typed profile 목록의 마지막 항목이며, 호출자는 이어지는 smtp- ID와
            # 전용 패널로 실제 선택 결과를 다시 검증한다.
            if ($itemName -ne "표준 메일 서버") {
                throw "$context 항목을 찾지 못했습니다: $itemName"
            }
            [LIGClawSmokeWindowApi]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
            $comboBox.SetFocus()
            [System.Windows.Forms.SendKeys]::SendWait("{END}")
            Start-Sleep -Milliseconds 200
            return
        }
        $selectedItems = @($selection.Current.GetSelection())
        if ($selectedItems.Count -ne 1 -or $selectedItems[0].Current.Name -ne $itemName) {
            throw "$context 선택이 반영되지 않았습니다: $itemName"
        }
    }

    function Assert-KeyboardControl(
        $element,
        $expectedControlType,
        [string]$name,
        [string]$context) {
        if ($element.Current.ControlType -ne $expectedControlType) {
            throw "$context 컨트롤 형식이 올바르지 않습니다: $name"
        }
        if (-not $element.Current.IsEnabled -or -not $element.Current.IsKeyboardFocusable) {
            throw "$context 키보드로 사용할 수 없는 컨트롤입니다: $name"
        }
    }

    function Set-ProtectedTextByKeyboard($element, [string]$value, [string]$context) {
        if (-not [bool]$element.GetCurrentPropertyValue(
                [System.Windows.Automation.AutomationElement]::IsPasswordProperty)) {
            throw "$context 보호 입력으로 노출되지 않았습니다."
        }
        $focusDeadline = (Get-Date).AddSeconds(3)
        do {
            [LIGClawSmokeWindowApi]::SetForegroundWindow($process.MainWindowHandle) | Out-Null
            $element.SetFocus()
            Start-Sleep -Milliseconds 100
        } while ((-not $element.Current.HasKeyboardFocus -or
                  [LIGClawSmokeWindowApi]::GetForegroundWindow() -ne $process.MainWindowHandle) -and
                 (Get-Date) -lt $focusDeadline)
        if (-not $element.Current.HasKeyboardFocus -or
            [LIGClawSmokeWindowApi]::GetForegroundWindow() -ne $process.MainWindowHandle) {
            throw "$context 키보드 포커스를 받지 못했습니다."
        }
        [System.Windows.Forms.SendKeys]::SendWait($value)
    }

    function Wait-ValidationMessage([string]$message, [string]$context) {
        $validation = $null
        $validationDeadline = (Get-Date).AddSeconds(5)
        do {
            Start-Sleep -Milliseconds 100
            $validation = Find-ByName $message
        } while ((-not $validation -or $validation.Current.IsOffscreen) -and
                 (Get-Date) -lt $validationDeadline)
        if (-not $validation -or $validation.Current.IsOffscreen) {
            throw "$context validation을 확인하지 못했습니다: $message"
        }
    }

    function Invoke-SmtpConnectionsPath([string]$layoutName) {
        $newProfile = Wait-VisibleByName "새 연결 설정" "$layoutName 연결 설정"
        $newProfile.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $profileKind = Wait-VisibleScrollableByName "연결 종류" "$layoutName 연결 설정"
        Assert-KeyboardControl $profileKind ([System.Windows.Automation.ControlType]::ComboBox) `
            "연결 종류" "$layoutName 연결 설정"
        Select-ComboBoxItem $profileKind "표준 메일 서버" "$layoutName 표준 메일 서버"

        $profileId = Wait-VisibleScrollableControlByName `
            "연결 식별자" ([System.Windows.Automation.ControlType]::Edit) `
            "$layoutName 표준 메일 서버"
        $profileIdValue = $profileId.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        if (-not $profileIdValue.StartsWith("smtp-", [StringComparison]::Ordinal)) {
            throw "$layoutName 표준 메일 서버 연결 식별자가 선택되지 않았습니다."
        }

        foreach ($control in @(
                [pscustomobject]@{ Name = "메일 서버 주소"; Type = [System.Windows.Automation.ControlType]::Edit },
                [pscustomobject]@{ Name = "메일 서버 포트"; Type = [System.Windows.Automation.ControlType]::Edit },
                [pscustomobject]@{ Name = "메일 서버 보안 연결 방식"; Type = [System.Windows.Automation.ControlType]::ComboBox },
                [pscustomobject]@{ Name = "메일 서버 고정 발신자"; Type = [System.Windows.Automation.ControlType]::Edit },
                [pscustomobject]@{ Name = "새 메일 서버 사용자명"; Type = [System.Windows.Automation.ControlType]::Edit },
                [pscustomobject]@{ Name = "새 메일 서버 암호"; Type = [System.Windows.Automation.ControlType]::Edit },
                [pscustomobject]@{ Name = "연결 설정 저장"; Type = [System.Windows.Automation.ControlType]::Button })) {
            $element = Wait-VisibleScrollableControlByName `
                $control.Name $control.Type "$layoutName typed SMTP"
            Assert-KeyboardControl $element $control.Type $control.Name "$layoutName typed SMTP"
        }
        $probe = Wait-VisibleScrollableControlByName `
            "메일 서버 암호화 연결과 로그인 확인" `
            ([System.Windows.Automation.ControlType]::Button) `
            "$layoutName typed SMTP"
        if ($probe.Current.IsEnabled) {
            throw "$layoutName 저장 전 typed SMTP probe가 예기치 않게 활성화됐습니다."
        }

        $tlsMode = Wait-VisibleScrollableByName "메일 서버 보안 연결 방식" "$layoutName 표준 메일 서버"
        $tlsSelection = $tlsMode.GetCurrentPattern(
            [System.Windows.Automation.SelectionPattern]::Pattern)
        $selectedTls = @($tlsSelection.Current.GetSelection())
        if ($selectedTls.Count -ne 1) {
            throw "$layoutName typed SMTP TLS 방식이 단일 선택 상태가 아닙니다."
        }

        $port = Wait-VisibleScrollableByName "메일 서버 포트" "$layoutName 표준 메일 서버"
        $port.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).SetValue("not-a-port")
        $username = Wait-VisibleScrollableByName "새 메일 서버 사용자명" "$layoutName 표준 메일 서버"
        $password = Wait-VisibleScrollableByName "새 메일 서버 암호" "$layoutName 표준 메일 서버"
        Set-ProtectedTextByKeyboard $username "smtpsmokeuser" "$layoutName SMTP 사용자명"
        Set-ProtectedTextByKeyboard $password "smtpsmokepassword" "$layoutName SMTP 암호"

        $save = Wait-VisibleScrollableByName "연결 설정 저장" "$layoutName 표준 메일 서버"
        $save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        # 포트 validation은 두 secret이 실제로 입력되었지만 Save handler가 즉시 지운 뒤,
        # SmtpConnectionManagementService를 호출하기 전에 중단됐음을 뜻한다.
        Wait-ValidationMessage "메일 서버 포트는 1부터 65535 사이의 숫자여야 합니다." `
            "$layoutName typed SMTP secret 입력"

        $port = Wait-VisibleScrollableByName "메일 서버 포트" "$layoutName 표준 메일 서버"
        $port.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).SetValue("587")
        $save = Wait-VisibleScrollableByName "연결 설정 저장" "$layoutName 표준 메일 서버"
        $save.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        # 두 번째 Save가 credential validation에서 멈추면 첫 번째 Save 뒤 보호 입력이
        # 비워졌고 Credential Manager/프로필 저장/network probe가 호출되지 않은 것이다.
        Wait-ValidationMessage "교체할 메일 서버 사용자명과 암호를 모두 입력해 주세요." `
            "$layoutName typed SMTP secret clearing"

        $newProfile = Wait-VisibleScrollableByName "새 연결 설정" "$layoutName 연결 설정 복귀"
        $newProfile.GetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    }

    function Invoke-FeatureBuilderPath([string]$layoutName) {
        $builderButton = Wait-VisibleByName "새 기능 만들기" "$layoutName 기능"
        if ($builderButton.Current.ControlType -ne [System.Windows.Automation.ControlType]::Button) {
            throw "$layoutName Capabilities의 '새 기능 만들기'가 버튼이 아닙니다."
        }
        $builderButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

        $builderPage = Wait-VisibleByName "기능 만들기 페이지" "$layoutName 기능 만들기"
        $scrollCondition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty,
            $true)
        $builderScrollElement = $null
        foreach ($candidate in $builderPage.FindAll(
                [System.Windows.Automation.TreeScope]::Descendants,
                $scrollCondition)) {
            if ($candidate.Current.ControlType -eq [System.Windows.Automation.ControlType]::Pane) {
                $builderScrollElement = $candidate
                break
            }
        }
        if (-not $builderScrollElement) {
            throw "$layoutName Feature Builder에서 세로 스크롤 경계를 찾지 못했습니다."
        }
        $builderScroll = $builderScrollElement.GetCurrentPattern(
            [System.Windows.Automation.ScrollPattern]::Pattern)
        if ($builderScroll.Current.HorizontallyScrollable) {
            throw "$layoutName Feature Builder에 예상하지 않은 가로 스크롤이 생겼습니다."
        }
        if ($builderScroll.Current.VerticalViewSize -lt 0 -or
            $builderScroll.Current.VerticalViewSize -gt 100) {
            throw "$layoutName Feature Builder의 세로 스크롤 viewport가 범위를 벗어났습니다."
        }

        $requirementInput = Wait-VisibleByName "새 기능 요구사항" "$layoutName Feature Builder"
        $requirementValue = $requirementInput.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)
        $safeRequirement = "파일 메타데이터를 확인하고 결과를 알림으로 알려 줘"
        $requirementValue.SetValue($safeRequirement)
        if ($requirementValue.Current.Value -ne $safeRequirement) {
            throw "$layoutName Feature Builder 요구사항 입력이 반영되지 않았습니다."
        }

        $generateButton = Find-ByName "기능 초안 생성"
        $scrollAttempts = 0
        while ((-not $generateButton -or $generateButton.Current.IsOffscreen) -and
               $builderScroll.Current.VerticallyScrollable -and $scrollAttempts -lt 12) {
            $builderScroll.ScrollVertical([System.Windows.Automation.ScrollAmount]::SmallIncrement)
            Start-Sleep -Milliseconds 100
            $generateButton = Find-ByName "기능 초안 생성"
            $scrollAttempts++
        }
        if (-not $generateButton -or $generateButton.Current.IsOffscreen -or
            -not $generateButton.Current.IsEnabled) {
            throw "$layoutName Feature Builder의 생성 버튼을 사용할 수 없습니다."
        }
        # 모델 생성은 smoke 범위가 아니다. InvokePattern 지원만 확인하고 절대 Invoke하지 않는다.
        $generateButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern) | Out-Null

        if ($builderScroll.Current.VerticallyScrollable) {
            $scrollPercent = $builderScroll.Current.VerticalScrollPercent
            if ($scrollPercent -lt 0 -or $scrollPercent -gt 100) {
                throw "$layoutName Feature Builder의 세로 스크롤 위치가 범위를 벗어났습니다."
            }
            $builderScroll.SetScrollPercent(
                [System.Windows.Automation.ScrollPattern]::NoScroll,
                0)
        } elseif ($builderScroll.Current.VerticalScrollPercent -ne
                  [System.Windows.Automation.ScrollPattern]::NoScroll) {
            throw "$layoutName Feature Builder의 비활성 스크롤 경계가 올바르지 않습니다."
        }

        $backButton = Wait-VisibleByName "기능 목록으로 돌아가기" "$layoutName 기능 만들기"
        $backButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-VisibleByName "기능 관리 페이지" "$layoutName 기능 복귀" | Out-Null
    }

    function Invoke-ComposerFeatureBuilderHandoff([string]$layoutName) {
        $requestInput = Wait-VisibleByName "요청 내용" "$layoutName Composer handoff"
        $requestValue = $requestInput.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)
        $handoffButton = Wait-VisibleByName `
            "이 요청을 기능으로 만들기" "$layoutName Composer handoff"
        if (-not $handoffButton.Current.IsEnabled) {
            throw "$layoutName Composer handoff 버튼을 사용할 수 없습니다."
        }

        $requestValue.SetValue("")
        $handoffButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-ValidationMessage "기능으로 만들 요청을 먼저 입력해 주세요." `
            "$layoutName Composer empty handoff"

        $safeRequirement = "파일 메타데이터를 확인하고 로컬 알림으로 알려 줘"
        $requestValue.SetValue($safeRequirement)
        $handoffButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-VisibleByName "기능 만들기 페이지" "$layoutName 대화 입력 기능 만들기" | Out-Null
        $builderRequirementReady = $false
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            $builderRequirement = Wait-VisibleByName "새 기능 요구사항" "$layoutName Composer handoff Builder"
            $builderValue = $builderRequirement.GetCurrentPattern(
                [System.Windows.Automation.ValuePattern]::Pattern)
            if ($builderValue.Current.Value -eq $safeRequirement -and -not $builderValue.Current.IsReadOnly) {
                $builderRequirementReady = $true
                break
            }
            Start-Sleep -Milliseconds 100
        }
        if (-not $builderRequirementReady) {
            throw "$layoutName Composer 입력이 편집 가능한 Builder 요구사항으로 복사되지 않았습니다."
        }
        Wait-VisibleByName `
            "대화 입력을 가져왔습니다. 내용을 확인한 뒤 직접 초안 생성을 눌러 주세요." `
            "$layoutName Composer handoff no-auto-generate" | Out-Null

        $backButton = Wait-VisibleByName "기능 목록으로 돌아가기" "$layoutName 대화 입력 기능 만들기"
        $backButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        Wait-VisibleByName "기능 관리 페이지" "$layoutName 대화 입력 기능 복귀" | Out-Null
        $homeButton = Wait-VisibleByName "대화 홈" "$layoutName Composer 원문 보존"
        $homeButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
        $requestInput = Wait-VisibleByName "요청 내용" "$layoutName Composer 원문 보존"
        $requestValue = $requestInput.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern)
        if ($requestValue.Current.Value -ne $safeRequirement) {
            throw "$layoutName Composer handoff가 원래 요청 입력을 변경했습니다."
        }
    }

    [LIGClawSmokeWindowApi]::SetWindowPos(
        $process.MainWindowHandle,
        [IntPtr]::Zero,
        100,
        100,
        760,
        500,
        0x0040) | Out-Null
    Start-Sleep -Milliseconds 500
    foreach ($requiredControl in @("요청 내용", "시스템 상태 확인", "이 요청을 기능으로 만들기", "보내기")) {
        $element = Find-ByName $requiredControl
        if (-not $element -or $element.Current.IsOffscreen) {
            throw "760×500 compact hero layout에서 핵심 컨트롤이 보이지 않습니다: $requiredControl"
        }
    }
    Invoke-ComposerFeatureBuilderHandoff "760×500"
    Write-Output "CapabilityBuilderHandoff=passed-at-760x500"
    [LIGClawSmokeWindowApi]::SetWindowPos(
        $process.MainWindowHandle,
        [IntPtr]::Zero,
        100,
        100,
        1060,
        650,
        0x0040) | Out-Null
    Start-Sleep -Milliseconds 500

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
    $baseUrlInput = Find-ByName "모델 서버 기본 주소"
    $baseUrlInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("not-a-url")
    $connectionTestButton = Find-ByName "모델 연결 테스트"
    $connectionTestButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $baseUrlError = $null
    $validationDeadline = (Get-Date).AddSeconds(5)
    do {
        Start-Sleep -Milliseconds 100
        $baseUrlError = Find-ByName "http:// 또는 https://로 시작하는 주소를 입력해 주세요."
    } while ((-not $baseUrlError -or $baseUrlError.Current.IsOffscreen) -and
             (Get-Date) -lt $validationDeadline)
    if (-not $baseUrlError -or $baseUrlError.Current.IsOffscreen) {
        throw "설정의 Base URL 필드 오류가 입력 위치에 표시되지 않았습니다."
    }
    Invoke-Navigation "실행 활동" "실행 활동 페이지"
    if (-not (Find-ByName "실행 상태 필터")) { throw "실행 활동 상태 필터가 없습니다." }
    if (-not (Find-ByName "실행 활동 정렬")) { throw "실행 활동 정렬이 없습니다." }
    Invoke-Navigation "기능 관리" "기능 관리 페이지"
    if (-not (Find-ByName "기능 상태 필터")) { throw "기능 상태 필터가 없습니다." }
    if (-not (Find-ByName "기능 정렬")) { throw "기능 정렬이 없습니다." }
    if (-not (Find-ByName "설치된 기능 목록")) { throw "기능 목록이 없습니다." }
    Invoke-FeatureBuilderPath "일반"
    Invoke-Navigation "연결 설정 관리" "연결 설정 관리 페이지"
    if (-not (Find-ByName "저장된 연결 설정 목록")) { throw "연결 설정 목록이 없습니다." }
    if (-not (Find-ByName "연결 식별자")) { throw "연결 설정 편집기가 없습니다." }
    Invoke-SmtpConnectionsPath "일반"
    Invoke-Navigation "기억 관리" "기억 관리 페이지"
    if (-not (Find-ByName "기억 검색")) { throw "기억 검색 입력이 없습니다." }
    if (-not (Find-ByName "기억 정렬")) { throw "기억 정렬이 없습니다." }
    Invoke-Navigation "예약 관리" "예약 관리 페이지"
    if (-not (Find-ByName "완료·취소된 예약도 표시")) { throw "예약 상태 필터가 없습니다." }
    if (-not (Find-ByName "예약 정렬")) { throw "예약 정렬이 없습니다." }
    Invoke-Navigation "자동 작업 관리" "자동 작업 관리 페이지"
    if (-not (Find-ByName "저장된 자동 작업 목록")) { throw "자동 작업 목록이 없습니다." }
    if (-not (Find-ByName "새 자동 작업")) { throw "자동 작업 생성 동작이 없습니다." }

    $topLevelWindowCount = @($root.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $processCondition) | Where-Object {
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Window
        }).Count
    if ($topLevelWindowCount -ne 1) {
        throw "관리 페이지 탐색 중 예상하지 않은 최상위 창이 열렸습니다: $topLevelWindowCount"
    }

    foreach ($navigationName in @("대화 홈", "새 대화 시작", "설정", "실행 활동", "기능 관리", "연결 설정 관리", "기억 관리", "예약 관리", "자동 작업 관리")) {
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

    foreach ($requiredControl in @("요청 내용", "시스템 상태 확인", "이 요청을 기능으로 만들기", "보내기")) {
        $element = Find-ByName $requiredControl
        if (-not $element -or $element.Current.IsOffscreen) {
            throw "760×500 compact hero layout에서 핵심 컨트롤이 보이지 않습니다: $requiredControl"
        }
    }

    Invoke-Navigation "설정" "설정 페이지"
    foreach ($requiredControl in @("모델 서버 기본 주소", "모델 연결 테스트", "저장")) {
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
    Invoke-Navigation "기능 관리" "기능 관리 페이지"
    foreach ($requiredControl in @("기능 파일 가져오기", "기능 상태 필터", "기능 정렬", "설치된 기능 목록")) {
        $element = Find-ByName $requiredControl
        if (-not $element -or $element.Current.IsOffscreen) {
            throw "760×500 Capabilities 화면에서 핵심 컨트롤이 보이지 않습니다: $requiredControl"
        }
    }
    Invoke-FeatureBuilderPath "760×500"
    Invoke-Navigation "연결 설정 관리" "연결 설정 관리 페이지"
    foreach ($requiredControl in @("새 연결 설정", "저장된 연결 설정 목록", "연결 식별자")) {
        $element = Find-ByName $requiredControl
        if (-not $element -or $element.Current.IsOffscreen) {
            throw "760×500 Connections 화면에서 핵심 컨트롤이 보이지 않습니다: $requiredControl"
        }
    }
    Invoke-SmtpConnectionsPath "760×500"
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
    Invoke-Navigation "자동 작업 관리" "자동 작업 관리 페이지"
    foreach ($requiredControl in @("새 자동 작업", "저장된 자동 작업 목록")) {
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
        IntegratedPages = 8
        TopLevelWindows = $topLevelWindowCount
        CompactLayout = "760x500"
        InputFocusRestored = $focusResult
        FieldValidation = "passed"
        ManagementSorting = "passed"
        ConversationBoundary = "passed"
        KeyboardNavigation = "passed"
        PrecisionWorkspace = "passed"
        TypedSmtpConnections = "passed"
        SmtpSecretClearing = "passed-without-mutation"
        CapabilitySmtpReviewContract = "passed"
        CapabilityBuilderNavigationContract = "passed"
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
