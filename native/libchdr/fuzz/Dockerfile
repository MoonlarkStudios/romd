# Disposable native-architecture Linux toolchain for the libchdr libFuzzer targets; scripts/native/fuzz.sh builds and runs it.
# The image holds only the compiler: the harness and the pinned libchdr sources are mounted read-only at run time.
FROM ubuntu:24.04@sha256:534baea6a22c03a63003dbc8dbe78fe34bc0d7e595d9a9dc9834884ff530eb55
RUN apt-get update \
 && apt-get install -y --no-install-recommends clang-18 libclang-rt-18-dev llvm-18 cmake ninja-build \
 && rm -rf /var/lib/apt/lists/*
# clang, llvm-symbolizer (readable sanitizer stacks) and the libFuzzer runtime come from LLVM 18.
ENV PATH=/usr/lib/llvm-18/bin:$PATH
