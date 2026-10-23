[CmdletBinding()]
param(
    [string]$OutputDirectory = 'dist\ui-samples'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Align-Value {
    param(
        [Parameter(Mandatory)]
        [int]$Value,

        [int]$Alignment = 8
    )

    return ($Value + $Alignment - 1) -band -$Alignment
}

function Write-MinidumpDirectory {
    param(
        [Parameter(Mandatory)]
        [System.IO.BinaryWriter]$Writer,

        [Parameter(Mandatory)]
        [uint32]$StreamType,

        [Parameter(Mandatory)]
        [uint32]$DataSize,

        [Parameter(Mandatory)]
        [uint32]$Rva
    )

    $Writer.Write($StreamType)
    $Writer.Write($DataSize)
    $Writer.Write($Rva)
}

function New-MinidumpSample {
    param(
        [Parameter(Mandatory)]
        [uint32]$BugCheckCode,

        [Parameter(Mandatory)]
        [uint64[]]$BugCheckParameters,

        [Parameter(Mandatory)]
        [uint64]$ModuleBase,

        [Parameter(Mandatory)]
        [uint32]$ModuleSize,

        [Parameter(Mandatory)]
        [string]$ModulePath
    )

    if ($BugCheckParameters.Count -ne 4) {
        throw 'BugCheck 参数必须正好包含四项。'
    }

    $headerRva = 0
    $directoryRva = 32
    $streamCount = 3
    $systemInfoRva = Align-Value ($directoryRva + ($streamCount * 12))
    $systemInfoSize = 56
    $bugCheckRva = Align-Value ($systemInfoRva + $systemInfoSize)
    $bugCheckSize = 36
    $moduleListRva = Align-Value ($bugCheckRva + $bugCheckSize)
    $moduleEntryRva = $moduleListRva + 4
    $moduleEntrySize = 108
    $moduleNameRva = $moduleEntryRva + $moduleEntrySize
    $moduleNameBytes = [System.Text.Encoding]::Unicode.GetBytes($ModulePath)
    $fileSize = Align-Value ($moduleNameRva + 4 + $moduleNameBytes.Length + 2)

    $buffer = [byte[]]::new($fileSize)
    $stream = [System.IO.MemoryStream]::new($buffer, $true)
    $writer = [System.IO.BinaryWriter]::new(
        $stream,
        [System.Text.Encoding]::Unicode,
        $true)

    try {
        # MINIDUMP_HEADER (32 bytes).
        $stream.Position = $headerRva
        $writer.Write([uint32]0x504D444D) # "MDMP"
        $writer.Write([uint32]0x0000A793) # MINIDUMP_VERSION
        $writer.Write([uint32]$streamCount)
        $writer.Write([uint32]$directoryRva)
        $writer.Write([uint32]0)          # CheckSum
        $writer.Write([uint32]1767225600) # Fixed timestamp for reproducible samples.
        $writer.Write([uint64]0)          # MiniDumpNormal

        # Three documented directory records, each 12 bytes.
        $stream.Position = $directoryRva
        Write-MinidumpDirectory $writer 7 $systemInfoSize $systemInfoRva
        Write-MinidumpDirectory $writer 0x0505 $bugCheckSize $bugCheckRva
        Write-MinidumpDirectory $writer 4 (4 + $moduleEntrySize) $moduleListRva

        # MINIDUMP_SYSTEM_INFO for Windows 11 x64. The CPU union remains zero-filled.
        $stream.Position = $systemInfoRva
        $writer.Write([uint16]9)       # PROCESSOR_ARCHITECTURE_AMD64
        $writer.Write([uint16]6)       # ProcessorLevel
        $writer.Write([uint16]0xA00)   # ProcessorRevision
        $writer.Write([byte]16)        # NumberOfProcessors
        $writer.Write([byte]1)         # VER_NT_WORKSTATION
        $writer.Write([uint32]10)      # MajorVersion
        $writer.Write([uint32]0)       # MinorVersion
        $writer.Write([uint32]22631)   # BuildNumber
        $writer.Write([uint32]2)       # VER_PLATFORM_WIN32_NT
        $writer.Write([uint32]0)       # CSDVersionRva
        $writer.Write([uint16]0)       # SuiteMask
        $writer.Write([uint16]0)       # Reserved2

        # BlueScreenHelper's BugCheck information stream: ULONG code + four ULONG64s.
        $stream.Position = $bugCheckRva
        $writer.Write($BugCheckCode)
        foreach ($parameter in $BugCheckParameters) {
            $writer.Write([uint64]$parameter)
        }

        # MINIDUMP_MODULE_LIST with one fixed-width MINIDUMP_MODULE record.
        $stream.Position = $moduleListRva
        $writer.Write([uint32]1)
        $stream.Position = $moduleEntryRva
        $writer.Write($ModuleBase)
        $writer.Write($ModuleSize)
        $writer.Write([uint32]0) # CheckSum
        $writer.Write([uint32]0) # TimeDateStamp
        $writer.Write([uint32]$moduleNameRva)
        $stream.Position = $moduleEntryRva + $moduleEntrySize

        # MINIDUMP_STRING stores a UTF-16 byte count excluding the trailing NUL.
        $writer.Write([uint32]$moduleNameBytes.Length)
        $writer.Write($moduleNameBytes)
        $writer.Write([uint16]0)
        $writer.Flush()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }

    return ,$buffer
}

function Test-MinidumpSample {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [uint32]$ExpectedBugCheckCode
    )

    $stream = [System.IO.File]::OpenRead($Path)
    $reader = [System.IO.BinaryReader]::new($stream)
    try {
        if ($reader.ReadUInt32() -ne [uint32]0x504D444D) {
            throw "样本不是有效的 MDMP 文件：$Path"
        }

        $stream.Position = 8
        $streamCount = $reader.ReadUInt32()
        $directoryRva = $reader.ReadUInt32()
        if ($streamCount -ne 3 -or $directoryRva -ne 32) {
            throw "样本目录头不符合预期：$Path"
        }

        $foundTypes = [System.Collections.Generic.HashSet[uint32]]::new()
        $bugCheckRva = [uint32]0
        $bugCheckSize = [uint32]0
        $stream.Position = $directoryRva
        for ($index = 0; $index -lt $streamCount; $index++) {
            $streamType = $reader.ReadUInt32()
            $dataSize = $reader.ReadUInt32()
            $rva = $reader.ReadUInt32()
            [void]$foundTypes.Add($streamType)
            if ([uint64]$rva + [uint64]$dataSize -gt [uint64]$stream.Length) {
                throw "样本流 0x$($streamType.ToString('X')) 超出文件边界：$Path"
            }
            if ($streamType -eq 0x0505) {
                $bugCheckRva = $rva
                $bugCheckSize = $dataSize
            }
        }

        foreach ($requiredType in [uint32[]]@(7, 4, 0x0505)) {
            if (-not $foundTypes.Contains($requiredType)) {
                throw "样本缺少流 0x$($requiredType.ToString('X'))：$Path"
            }
        }
        if ($bugCheckSize -lt 36) {
            throw "BugCheck 流长度不足：$Path"
        }

        $stream.Position = $bugCheckRva
        $actualCode = $reader.ReadUInt32()
        if ($actualCode -ne $ExpectedBugCheckCode) {
            throw "BugCheck 代码校验失败：期望 0x$($ExpectedBugCheckCode.ToString('X8'))，实际 0x$($actualCode.ToString('X8'))。"
        }
    }
    finally {
        $reader.Dispose()
        $stream.Dispose()
    }
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$targetDirectory = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    [System.IO.Path]::GetFullPath($OutputDirectory)
}
else {
    [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null

$knownModuleBase = [Convert]::ToUInt64('FFFFF80140000000', 16)
$knownCode = [uint32]0x116
$knownParameters = [uint64[]]@(
    [Convert]::ToUInt64('FFFFC30D12345010', 16)
    ($knownModuleBase + 0x2A34)
    [Convert]::ToUInt64('FFFFFFFFC000009A', 16)
    [uint64]4
)
$knownBytes = New-MinidumpSample `
    -BugCheckCode $knownCode `
    -BugCheckParameters $knownParameters `
    -ModuleBase $knownModuleBase `
    -ModuleSize ([uint32]0x01000000) `
    -ModulePath 'C:\Windows\System32\drivers\nvlddmkm.sys'
$knownPath = Join-Path $targetDirectory 'Known-0x00000116-VIDEO_TDR_FAILURE.dmp'
[System.IO.File]::WriteAllBytes($knownPath, $knownBytes)
Test-MinidumpSample -Path $knownPath -ExpectedBugCheckCode $knownCode

$unknownModuleBase = [Convert]::ToUInt64('FFFFF80510000000', 16)
$unknownCode = [Convert]::ToUInt32('DEADC0DE', 16)
$unknownParameters = [uint64[]]@(
    ($unknownModuleBase + 0x900)
    [uint64]0x10
    [uint64]0
    [uint64]0
)
$unknownBytes = New-MinidumpSample `
    -BugCheckCode $unknownCode `
    -BugCheckParameters $unknownParameters `
    -ModuleBase $unknownModuleBase `
    -ModuleSize ([uint32]0x00200000) `
    -ModulePath 'C:\Windows\System32\drivers\mysteryfilter.sys'
$unknownPath = Join-Path $targetDirectory 'Unknown-0xDEADC0DE.dmp'
[System.IO.File]::WriteAllBytes($unknownPath, $unknownBytes)
Test-MinidumpSample -Path $unknownPath -ExpectedBugCheckCode $unknownCode

@(
    [pscustomobject]@{
        Scenario = '已知代码：VIDEO_TDR_FAILURE（长解决方案）'
        BugCheck = '0x00000116'
        Module = 'nvlddmkm.sys'
        Bytes = $knownBytes.Length
        Path = $knownPath
    }
    [pscustomobject]@{
        Scenario = '未知代码：通用定位与解决方案'
        BugCheck = '0xDEADC0DE'
        Module = 'mysteryfilter.sys'
        Bytes = $unknownBytes.Length
        Path = $unknownPath
    }
)
