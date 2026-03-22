if (-not (Get-Module -ListAvailable -Name Pester)) { throw 'Pester is required to run tests. Please install it via Install-Module Pester.' }
Import-Module Pester -ErrorAction Stop

Describe 'Crispy_Bills Version Rules' {
    BeforeAll {
        $script = Join-Path $PSScriptRoot 'version.ps1'
        . $script
    }

    It 'treats small changes as patch bumps' {
        $commits = @(
            [PSCustomObject]@{ Subject = 'fix: patch a bug'; Body = '' },
            [PSCustomObject]@{ Subject = 'docs: update release notes'; Body = '' },
            [PSCustomObject]@{ Subject = 'build: tweak packaging'; Body = '' }
        )

        (Get-BumpLevel -Commits $commits) | Should -Be 'patch'
        (Get-NextVersion -Current 'v1.1.0' -Level 'patch') | Should -Be 'v1.1.1'
    }

    It 'treats features as minor bumps' {
        $commits = @(
            [PSCustomObject]@{ Subject = 'feat: add split bill workflow'; Body = '' }
        )

        (Get-BumpLevel -Commits $commits) | Should -Be 'minor'
        (Get-NextVersion -Current 'v1.1.0' -Level 'minor') | Should -Be 'v1.2.0'
    }

    It 'treats explicit breaking changes as major bumps' {
        $commits = @(
            [PSCustomObject]@{ Subject = 'feat!: replace billing storage model'; Body = 'BREAKING CHANGE: old exports are incompatible' }
        )

        (Get-BumpLevel -Commits $commits) | Should -Be 'major'
        (Get-NextVersion -Current 'v1.1.0' -Level 'major') | Should -Be 'v2.0.0'
    }

    It 'never skips directly to a new minor during a major bump' {
        (Get-NextVersion -Current 'v1.1.0' -Level 'major') | Should -Not -Be 'v2.1.0'
    }
}