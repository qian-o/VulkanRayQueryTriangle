cmake_minimum_required(VERSION 3.24)
project(VulkanDescriptorHeapRayQueryRepro LANGUAGES CXX)

find_package(Vulkan 1.4 REQUIRED)

add_executable(vulkan_descriptor_heap_ray_query_repro main.cpp)
target_compile_features(vulkan_descriptor_heap_ray_query_repro PRIVATE cxx_std_20)
target_link_libraries(vulkan_descriptor_heap_ray_query_repro PRIVATE Vulkan::Vulkan)

if(MSVC)
    target_compile_options(vulkan_descriptor_heap_ray_query_repro PRIVATE /W4 /permissive-)
else()
    target_compile_options(vulkan_descriptor_heap_ray_query_repro PRIVATE -Wall -Wextra -Wpedantic)
endif()

add_custom_command(TARGET vulkan_descriptor_heap_ray_query_repro POST_BUILD
    COMMAND ${CMAKE_COMMAND} -E copy_if_different
        "${CMAKE_CURRENT_SOURCE_DIR}/ray_query.spv"
        "$<TARGET_FILE_DIR:vulkan_descriptor_heap_ray_query_repro>/ray_query.spv"
)
