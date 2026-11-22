param([int]$Seconds = 300)

$rate = 44100
$channels = 1
$bits = 16
$dataSize = $rate * $channels * ($bits / 8) * $Seconds

$path = Join-Path (Get-Location) "scratch\silence.wav"
$stream = [System.IO.File]::Create($path)
$writer = New-Object System.IO.BinaryWriter($stream)

$writer.Write([System.Text.Encoding]::ASCII.GetBytes("RIFF"))
$writer.Write([int](36 + $dataSize))
$writer.Write([System.Text.Encoding]::ASCII.GetBytes("WAVEfmt "))
$writer.Write([int]16)
$writer.Write([int16]1)
$writer.Write([int16]$channels)
$writer.Write([int]$rate)
$writer.Write([int]($rate * $channels * ($bits / 8)))
$writer.Write([int16]($channels * ($bits / 8)))
$writer.Write([int16]$bits)
$writer.Write([System.Text.Encoding]::ASCII.GetBytes("data"))
$writer.Write([int]$dataSize)

$buffer = New-Object byte[] 65536
for ($written = 0; $written -lt $dataSize; $written += $buffer.Length) {
    $writer.Write($buffer)
}

$writer.Flush()
$writer.Close()
$stream.Close()

Write-Output $path
