param([string]$Root=(Split-Path $PSScriptRoot -Parent))
$ErrorActionPreference='Stop'
$Root=(Resolve-Path $Root).Path
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$destination=Join-Path $Root ".cache/knowledge-baseline/p19-$stamp"
New-Item -ItemType Directory -Path "$destination/backup/data"|Out-Null
$files=@(Get-ChildItem (Join-Path $Root 'data') -File | Where-Object {$_.Name -match '^(memory_|chat_log|important_memories).*\.(json|jsonl)$'})
$inventory=@();$memory=@()
foreach($file in $files){
 $before=(Get-FileHash $file.FullName -Algorithm SHA256).Hash
 $copy=Join-Path "$destination/backup/data" $file.Name
 Copy-Item -LiteralPath $file.FullName -Destination $copy
 if((Get-FileHash $copy -Algorithm SHA256).Hash-ne$before -or (Get-FileHash $file.FullName -Algorithm SHA256).Hash-ne$before){throw 'Source changed during backup; repeat preflight.'}
 $inventory+=@{name=$file.Name;bytes=$file.Length;sha256=$before}
 if($file.Name -like 'memory_*.json'){
  $state=Get-Content $copy -Raw|ConvertFrom-Json
  $records=@($state.Records);$archive=[IO.Path]::ChangeExtension($file.FullName,$null)+'.archive.jsonl'
  if(Test-Path $archive){$records+=@(Get-Content $archive|Where-Object{$_}|ForEach-Object{$_|ConvertFrom-Json})}
  $ids=@($records|ForEach-Object{$_.Id});$missing=@($records|ForEach-Object{ @($_.Sources)+@($_.superseded_by) }|Where-Object{$_ -and $_ -notin $ids}|Sort-Object -Unique)
  $memory+=@{file=$file.Name;records=$records.Count;duplicate_ids=@($ids|Group-Object|Where-Object Count -gt 1).Count;unresolved_references=$missing.Count;version=$state.Version;imported_chat_entries=$state.imported_chat_entries}
 }
}
$report=[ordered]@{created_utc=[DateTime]::UtcNow.ToString('O');root=$Root;backup="$destination/backup";files=$inventory;memory=$memory;activation_performed=$false;mode_target='sql';graph_default=$false;note='Read-only inventory and byte-verified backup. Final source checks and writer exclusion are still required for activation. No private text included.'}
$report|ConvertTo-Json -Depth 8|Set-Content "$destination/preflight.json" -Encoding utf8
Write-Output "$destination/preflight.json"
$memory|ForEach-Object{[pscustomobject]$_}|Format-Table file,records,duplicate_ids,unresolved_references,imported_chat_entries
