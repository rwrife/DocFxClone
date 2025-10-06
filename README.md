# DocFX Clone Utility

An intelligent git cloning utility for DocFX projects that uses sparse checkout to download only the files needed by your documentation project.

### Usage

Clone a repository and parse its DocFX project:

```bash
DocFxClone clone https://github.com/user/repo.git docs/docfx.json
```

With options:

```bash
DocFxClone clone https://github.com/user/repo.git docs/docfx.json --output ./my-docs --branch main --silent
```
