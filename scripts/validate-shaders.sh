#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
shader_root="$(cd "${1:-$repo_root/../ksa-linux/Content/Core/Shaders}" && pwd)"
glslc_bin="${GLSLC:-glslc}"
spirv_val_bin="${SPIRV_VAL:-spirv-val}"
glslang_bin="${GLSLANG_VALIDATOR:-glslangValidator}"

for tool in dotnet "$glslc_bin" "$spirv_val_bin"; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    printf 'Required shader validation tool not found: %s\n' "$tool" >&2
    exit 2
  fi
done

planet_source="$shader_root/Planet"
for file in Planet.frag Planet.vert PlanetTessEvaluation.tese PlanetTessControl.tesc; do
  if [[ ! -f "$planet_source/$file" ]]; then
    printf 'Required source shader is missing: %s\n' "$planet_source/$file" >&2
    exit 2
  fi
done

temporary_root="$(mktemp -d "${TMPDIR:-/tmp}/blasto-shader-validation.XXXXXX")"
trap 'rm -rf "$temporary_root"' EXIT

dotnet run --project "$repo_root/Blasto.Tests/Blasto.Tests.csproj" \
  --configuration Release --no-restore -- \
  --export-shaders "$shader_root" "$temporary_root/export"

declare -a variants=(standard ray_queries)

prepare_link_sources() {
  local source_set="$1"
  local destination="$2"
  mkdir -p "$destination"

  for shader in Planet.vert PlanetTessControl.tesc PlanetTessEvaluation.tese Planet.frag; do
    local source
    if [[ "$source_set" == stock ]]; then
      source="$planet_source/$shader"
    elif [[ "$shader" == PlanetTessControl.tesc ]]; then
      source="$planet_source/$shader"
    else
      source="$temporary_root/export/Planet/$shader"
    fi
    awk 'NR == 1 { print; print "#extension GL_GOOGLE_include_directive : require"; next } { print }' \
      "$source" > "$destination/$shader"
  done
}

is_expected_stock_sampler_mismatch() {
  local log="$1"
  local error_count
  error_count="$(grep -Ec '^ERROR:' "$log" || true)"
  [[ "$error_count" == 1 ]] \
    && grep -Fq 'ERROR: Linking tessellation evaluation stage: Types must match:' "$log" \
    && grep -Fq 'vertex stage: " unexpected planetNormalTexture"' "$log" \
    && grep -Fq 'tessellation evaluation stage: " unexpected planetNormalTexture"' "$log"
}

for variant in "${variants[@]}"; do
  variant_dir="$temporary_root/$variant"
  mkdir -p "$variant_dir"

  compiler_args=(--target-env=vulkan1.2 -I"$planet_source")
  linker_args=(-V -l -I"$planet_source")
  if [[ "$variant" == ray_queries ]]; then
    compiler_args+=(-DRAY_QUERIES_SUPPORTED=1)
    linker_args+=(-DRAY_QUERIES_SUPPORTED=1)
  fi

  for shader in Planet.vert PlanetTessEvaluation.tese Planet.frag; do
    output="$variant_dir/$shader.spv"
    "$glslc_bin" "${compiler_args[@]}" \
      "$temporary_root/export/Planet/$shader" -o "$output"
    "$spirv_val_bin" --target-env vulkan1.2 "$output"
    printf 'Validated %s (%s)\n' "$shader" "$variant"
  done

  if command -v "$glslang_bin" >/dev/null 2>&1; then
    baseline_dir="$variant_dir/stock-link-sources"
    baseline_log="$variant_dir/stock-link.log"
    prepare_link_sources stock "$baseline_dir"

    if (cd "$variant_dir" && "$glslang_bin" "${linker_args[@]}" \
      "$baseline_dir/Planet.vert" \
      "$baseline_dir/PlanetTessControl.tesc" \
      "$baseline_dir/PlanetTessEvaluation.tese" \
      "$baseline_dir/Planet.frag") >"$baseline_log" 2>&1; then
      normalize_sampler_mismatch=false
      printf 'Stock terrain stage baseline links (%s)\n' "$variant"
    elif is_expected_stock_sampler_mismatch "$baseline_log"; then
      normalize_sampler_mismatch=true
      printf 'Stock baseline has only the known planetNormalTexture sampler mismatch (%s).\n' "$variant" >&2
    else
      cat "$baseline_log" >&2
      printf 'Unexpected stock terrain stage baseline failure for %s.\n' "$variant" >&2
      exit 1
    fi

    link_sources="$variant_dir/link-sources"
    prepare_link_sources transformed "$link_sources"

    if [[ "$normalize_sampler_mismatch" == true ]]; then
      vertex_source="$link_sources/Planet.vert"
      sampler_references="$(grep -Fo 'planetNormalTexture' "$vertex_source" | wc -l | tr -d '[:space:]')"
      sampler_declarations="$(grep -Ec 'uniform[[:space:]]+texture2D[[:space:]]+planetNormalTexture;' "$vertex_source" || true)"
      if [[ "$sampler_references" != 1 || "$sampler_declarations" != 1 ]]; then
        printf 'Refusing sampler normalization: expected exactly one unused texture2D planetNormalTexture declaration in the vertex stage.\n' >&2
        exit 1
      fi

      normalized_vertex="$variant_dir/Planet.vert.normalized"
      sed -E 's/(uniform[[:space:]]+)texture2D([[:space:]]+planetNormalTexture;)/\1textureCube\2/' \
        "$vertex_source" > "$normalized_vertex"
      mv "$normalized_vertex" "$vertex_source"
      printf 'Normalized only the unused stock vertex sampler declaration in a temporary link copy (%s).\n' "$variant" >&2
    fi

    link_log="$variant_dir/link.log"
    if (cd "$variant_dir" && "$glslang_bin" "${linker_args[@]}" \
      "$link_sources/Planet.vert" \
      "$link_sources/PlanetTessControl.tesc" \
      "$link_sources/PlanetTessEvaluation.tese" \
      "$link_sources/Planet.frag") >"$link_log" 2>&1; then
      printf 'Linked transformed terrain stage interfaces (%s)\n' "$variant"
    else
      cat "$link_log" >&2
      printf 'Transformed terrain stage linking failed for %s.\n' "$variant" >&2
      exit 1
    fi
  else
    printf 'glslangValidator not found; stage linking skipped.\n' >&2
  fi
done

printf 'Shader validation passed for both variants.\n'
