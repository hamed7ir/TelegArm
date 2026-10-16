# Injected via CMAKE_PROJECT_INCLUDE for the x86 build. opus adds -msse4.1 to its SSE4.1 sources only when NOT MSVC
# (cl.exe compiles intrinsics without /arch); clang-cl needs the target feature per file. Runtime CPU detection
# (OPUS_X86_MAY_HAVE_SSE4_1, not PRESUME) still decides whether that code runs.
file(GLOB _opus_sse41
    ${CMAKE_CURRENT_SOURCE_DIR}/celt/x86/*_sse4_1.c
    ${CMAKE_CURRENT_SOURCE_DIR}/silk/x86/*_sse4_1.c
    ${CMAKE_CURRENT_SOURCE_DIR}/silk/fixed/x86/*_sse4_1.c
    ${CMAKE_CURRENT_SOURCE_DIR}/dnn/x86/*sse4_1*.c)
set_source_files_properties(${_opus_sse41} PROPERTIES COMPILE_OPTIONS "/clang:-msse4.1")
