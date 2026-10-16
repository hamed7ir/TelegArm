# Injected via CMAKE_PROJECT_INCLUDE (after project(), so compiler detection is untouched).
# opus PRESUME_NEON needs the MAY_HAVE_NEON(_INTR) defines for its NEON prototypes (Chromium ARM32 define set).
add_compile_definitions(OPUS_ARM_MAY_HAVE_NEON OPUS_ARM_MAY_HAVE_NEON_INTR)
