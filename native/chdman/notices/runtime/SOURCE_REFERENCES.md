# chdman runtime source references

chdman's static C and C++ runtime members come from the AlmaLinux 8.10 builder
packages pinned in `native/builder/linux-x64.rpm.txt` and
`native/builder/linux-arm64.rpm.txt`. The same versions are pinned for both
architectures.

| Linked material | Binary packages | Source package |
| --- | --- | --- |
| libstdc++, libgcc, `crtbegin.o`, `crtend.o` | `gcc-toolset-15-gcc`, `gcc-toolset-15-libstdc++-devel` 15.2.1-7.1.el8_10 | `gcc-toolset-15-gcc-15.2.1-7.1.el8_10.src.rpm` |
| `crt1.o`, `crti.o`, `crtn.o`, `libc_nonshared.a` | `glibc-devel` 2.28-251.el8_10.40 | `glibc-2.28-251.el8_10.40.src.rpm` |

The texts in `gcc-toolset-15/` are the license files installed by
`gcc-toolset-15-gcc`, and the texts in `glibc-2.28/` are those installed by the
builder's `glibc` package (2.28-251.el8_10.40). The image build copies them
unchanged from that builder.
