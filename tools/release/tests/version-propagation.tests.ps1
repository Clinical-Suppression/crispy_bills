Describe 'Release scripts - version propagation' {
    It 'release-mobile.ps1 exposes a Version parameter' {
        $content = Get-Content -Path (Join-Path $PSScriptRoot '..\release-mobile.ps1') -Raw -ErrorAction Stop
        $content | Should -Match '\[string\]\$Version'
    }
}
