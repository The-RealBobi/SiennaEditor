# Repositories and versions

- `The-RealBobi/SiennaEditor_prv`: private development history and implementation notes.
- `The-RealBobi/SiennaEditor`: clean public runtime source and build documentation.

The application's display name is **Sienna Editor**. GitHub repository identifiers omit the space by the maintainer's explicit preference.

`Directory.Build.props` owns the version. The development checkout's `public-files.txt` explicitly selects production files. Runtime sources are identical across both checkouts; private history, plans, local samples, diagnostics, build output and caches are never copied.

```sh
python3 /path/to/development/scripts/sync-public.py /path/to/public/SiennaEditor
bash scripts/sync-version.sh 0.1.0 /path/to/private/SiennaEditor /path/to/public/SiennaEditor
```

Use `scripts/sync-version.ps1` for equivalent version synchronization on Windows. Initialize/clone the public repository before copying. These scripts neither commit nor push; inspect the resulting changes before publishing. The copier rejects unrelated tracked public files rather than deleting them.

The production selection list and publication copier are kept only in the development repository. They are not included in distribution checkouts. Both codebases use GPL-3.0-only; see LICENSE.
