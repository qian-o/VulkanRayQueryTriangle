cmake_minimum_required(VERSION 3.24)
project(VulkanDescriptorHeapRayQueryRepro LANGUAGES CXX)

find_package(Vulkan 1.4 REQUIRED)
find_program(SLANGC_EXECUTABLE NAMES slangc REQUIRED)
find_program(SPIRV_VAL_EXECUTABLE NAMES spirv-val REQUIRED)
find_program(SPIRV_DIS_EXECUTABLE NAMES spirv-dis REQUIRED)

execute_process(
    COMMAND "${SLANGC_EXECUTABLE}" -version
    OUTPUT_VARIABLE SLANGC_VERSION_STDOUT
    ERROR_VARIABLE SLANGC_VERSION_STDERR
    COMMAND_ERROR_IS_FATAL ANY
)
string(STRIP "${SLANGC_VERSION_STDOUT}${SLANGC_VERSION_STDERR}" SLANGC_VERSION)
message(STATUS "Using Slang ${SLANGC_VERSION}: ${SLANGC_EXECUTABLE}")
if(NOT SLANGC_VERSION STREQUAL "2026.13")
    message(WARNING "The checked-in shader artifacts and reported result use Slang 2026.13")
endif()

set(SHADER_OUTPUT_DIR "${CMAKE_CURRENT_BINARY_DIR}/shader")
set(SHADER_SPV "${SHADER_OUTPUT_DIR}/ray_query.spv")
set(SHADER_ASM "${SHADER_OUTPUT_DIR}/ray_query.spvasm")

add_custom_command(
    OUTPUT "${SHADER_SPV}" "${SHADER_ASM}"
    COMMAND ${CMAKE_COMMAND} -E make_directory "${SHADER_OUTPUT_DIR}"
    COMMAND "${SLANGC_EXECUTABLE}"
        "${CMAKE_CURRENT_SOURCE_DIR}/ray_query.slang"
        -target spirv
        -profile glsl_460
        -entry main
        -stage compute
        -capability spirv_1_4
        -capability spvDescriptorHeapEXT
        -capability spvRayQueryKHR
        -o "${SHADER_SPV}"
    COMMAND "${SPIRV_VAL_EXECUTABLE}" --target-env vulkan1.4 "${SHADER_SPV}"
    COMMAND "${SPIRV_DIS_EXECUTABLE}" "${SHADER_SPV}" -o "${SHADER_ASM}"
    DEPENDS "${CMAKE_CURRENT_SOURCE_DIR}/ray_query.slang"
    VERBATIM
)

add_custom_target(ray_query_shader DEPENDS "${SHADER_SPV}" "${SHADER_ASM}")

add_executable(vulkan_descriptor_heap_ray_query_repro main.cpp)
add_dependencies(vulkan_descriptor_heap_ray_query_repro ray_query_shader)
target_compile_features(vulkan_descriptor_heap_ray_query_repro PRIVATE cxx_std_20)
target_link_libraries(vulkan_descriptor_heap_ray_query_repro PRIVATE Vulkan::Vulkan)

if(MSVC)
    target_compile_options(vulkan_descriptor_heap_ray_query_repro PRIVATE /W4 /permissive-)
else()
    target_compile_options(vulkan_descriptor_heap_ray_query_repro PRIVATE -Wall -Wextra -Wpedantic)
endif()

add_custom_command(TARGET vulkan_descriptor_heap_ray_query_repro POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
    "${SHADER_SPV}"
        "$<TARGET_FILE_DIR:vulkan_descriptor_heap_ray_query_repro>/ray_query.spv"
)
