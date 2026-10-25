# Before making this repository public

1. **Runners**: delete the `CI_RUNS_ON` repository variable (or set it to
   `"ubuntu-latest"`). Self-hosted runners must never serve a public repository.
2. **Secret scan** of the whole history, not only the tree
   (GitHub secret scanning, or `gitleaks detect --log-opts=--all`).
3. **Actions**: consider pinning third-party actions to a full commit SHA.
4. Settings ▸ Actions ▸ "Require approval for all outside collaborators" for
   workflows from forks.
5. Enable private vulnerability reporting (Security ▸ Advisories).
6. Re-read `README.md`: the install URLs point at this repository's releases.
