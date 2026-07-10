cmake_minimum_required(VERSION 3.24)
project(VulkanDescriptorHeapRayQueryRepro LANGUAGES CXX)

find_package(Vulkan 1.4 REQUIRED)

set(REPRO_SHADER_FILES
    ray_query_heap.slang
    ray_query_heap.spv
    ray_query_heap.spvasm
)
set_source_files_properties(${REPRO_SHADER_FILES} PROPERTIES HEADER_FILE_ONLY TRUE)

add_executable(vulkan_descriptor_heap_ray_query_repro
    main.cpp
    ${REPRO_SHADER_FILES}
)
target_compile_features(vulkan_descriptor_heap_ray_query_repro PRIVATE cxx_std_20)
target_link_libraries(vulkan_descriptor_heap_ray_query_repro PRIVATE Vulkan::Vulkan)

if(MSVC)
    target_compile_options(vulkan_descriptor_heap_ray_query_repro PRIVATE /W4 /permissive-)
else()
    target_compile_options(vulkan_descriptor_heap_ray_query_repro PRIVATE -Wall -Wextra -Wpedantic)
endif()

add_custom_command(TARGET vulkan_descriptor_heap_ray_query_repro POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/ray_query_heap.spv"
        "$<TARGET_FILE_DIR:vulkan_descriptor_heap_ray_query_repro>/ray_query_heap.spv"
)
