#!/usr/bin/env bash
set -euo pipefail

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT_DIR"

run_all=false
if [[ "${1:-}" == "--all" ]]; then
  run_all=true
fi

if [[ -n "${DELEGATION_CHANGED_FILES:-}" ]]; then
  changed_files="$DELEGATION_CHANGED_FILES"
elif $run_all; then
  changed_files=$'import-templates/MDI-To-EDRS-Template.xlsx\nsrc/MortalityReporting.ImportApi/Program.cs\ntests/MortalityReporting.ImportApi.Tests/MdiFhirMapperTests.cs\nREADME.md'
else
  changed_files="$( { git diff --name-only HEAD; git ls-files --others --exclude-standard; } | sort -u )"
fi

if [[ -z "$changed_files" ]]; then
  printf '%s\n' "Delegation loop: no relevant changes detected."
  exit 0
fi

has_change() {
  local pattern="$1"
  while IFS= read -r file; do
    [[ "$file" == $pattern ]] && return 0
    [[ "$pattern" == */** && "$file" == "${pattern%/**}"/* ]] && return 0
    [[ "$pattern" == *.md && "$file" == *.md ]] && return 0
  done <<< "$changed_files"
  return 1
}

templates_changed=false
source_changed=false
tests_changed=false
docs_changed=false

has_change 'import-templates/**' && templates_changed=true
has_change 'src/**' && source_changed=true
has_change 'tests/**' && tests_changed=true
has_change '*.md' && docs_changed=true

printf '%s\n' "Delegation loop scopes: templates=$templates_changed source=$source_changed tests=$tests_changed docs=$docs_changed"

if $source_changed || $tests_changed; then
  printf '%s\n' "Delegate: .NET build and full test suite"
  dotnet build MortalityReporting.sln --nologo
  dotnet test MortalityReporting.sln --no-build --nologo
elif $templates_changed; then
  printf '%s\n' "Delegate: template parser and FHIR mapping regression tests"
  dotnet build MortalityReporting.sln --nologo
  dotnet test tests/MortalityReporting.ImportApi.Tests/MortalityReporting.ImportApi.Tests.csproj --no-build --filter 'FullyQualifiedName~ImportFileParserTests|FullyQualifiedName~MdiFhirMapperTests' --nologo
fi

if $docs_changed; then
  printf '%s\n' "Delegate: markdown and whitespace validation"
  git diff --check
  test -s README.md
fi

printf '%s\n' "Delegation loop completed."
