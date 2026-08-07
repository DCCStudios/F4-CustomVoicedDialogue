# Generates Silence_1.wav .. Silence_10.wav — N seconds of 44.1 kHz mono
# 16-bit PCM silence — into the mod's staged Data tree.  These are the
# fallback voice files played while TTS audio for a line is still being
# synthesized; the engine keeps the subtitle up for the file's duration.
#
# Usage:  powershell -File generate.ps1 [-OutDir <path>]

param(
    [string]$OutDir = (Join-Path $PSScriptRoot "..\..\Compile\Sound\Voice\CustomVoicedDialogue")
)

$ErrorActionPreference = "Stop"

$sampleRate = 44100
$bitsPerSample = 16
$channels = 1
$blockAlign = $channels * ($bitsPerSample / 8)
$byteRate = $sampleRate * $blockAlign

New-Item -ItemType Directory -Force $OutDir | Out-Null

for ($seconds = 1; $seconds -le 10; $seconds++) {
    $dataSize = $sampleRate * $blockAlign * $seconds
    $path = Join-Path $OutDir ("Silence_{0}.wav" -f $seconds)

    $stream = [System.IO.File]::Create($path)
    try {
        $writer = New-Object System.IO.BinaryWriter($stream)
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("RIFF"))
        $writer.Write([uint32](36 + $dataSize))
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("WAVE"))
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("fmt "))
        $writer.Write([uint32]16)                 # fmt chunk size
        $writer.Write([uint16]1)                  # PCM
        $writer.Write([uint16]$channels)
        $writer.Write([uint32]$sampleRate)
        $writer.Write([uint32]$byteRate)
        $writer.Write([uint16]$blockAlign)
        $writer.Write([uint16]$bitsPerSample)
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("data"))
        $writer.Write([uint32]$dataSize)
        $writer.Write((New-Object byte[] $dataSize))
        $writer.Flush()
    }
    finally {
        $stream.Dispose()
    }
    Write-Host ("Wrote {0} ({1} bytes)" -f $path, (Get-Item $path).Length)
}

# Playback slots: Stream_00.wav .. Stream_23.wav, a quarter second of 48 kHz
# silence each.  The engine indexes loose files at startup, so a wav written
# mid-session is invisible to it; these exist purely to reserve indexed paths
# that freshly generated audio is copied into and played through the game's
# own audio system (3D positioning, volume sliders, normal mixing).  The
# plugin recreates any that go missing, but only the next launch indexes them.
$slotSampleRate = 48000
$slotBlockAlign = 2
$slotDataSize = [int]($slotSampleRate * $slotBlockAlign / 4)

for ($slot = 0; $slot -lt 24; $slot++) {
    $path = Join-Path $OutDir ("Stream_{0:d2}.wav" -f $slot)

    $stream = [System.IO.File]::Create($path)
    try {
        $writer = New-Object System.IO.BinaryWriter($stream)
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("RIFF"))
        $writer.Write([uint32](36 + $slotDataSize))
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("WAVE"))
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("fmt "))
        $writer.Write([uint32]16)
        $writer.Write([uint16]1)                  # PCM
        $writer.Write([uint16]1)                  # mono
        $writer.Write([uint32]$slotSampleRate)
        $writer.Write([uint32]($slotSampleRate * $slotBlockAlign))
        $writer.Write([uint16]$slotBlockAlign)
        $writer.Write([uint16]16)
        $writer.Write([System.Text.Encoding]::ASCII.GetBytes("data"))
        $writer.Write([uint32]$slotDataSize)
        $writer.Write((New-Object byte[] $slotDataSize))
        $writer.Flush()
    }
    finally {
        $stream.Dispose()
    }
}
Write-Host ("Wrote 24 playback slot files into {0}" -f $OutDir)
