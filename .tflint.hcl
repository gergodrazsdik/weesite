# TFLint configuration for the OpenTofu roots in infra/ (bootstrap and app).
# From the repo root: `tflint --init` once to fetch the pinned ruleset, then
# `tflint --recursive --config "$PWD/.tflint.hcl"` (the infra-static workflow does both).

plugin "terraform" {
  enabled = true
  preset  = "recommended"
}

plugin "azurerm" {
  enabled = true
  version = "0.33.0"
  source  = "github.com/terraform-linters/tflint-ruleset-azurerm"
}
