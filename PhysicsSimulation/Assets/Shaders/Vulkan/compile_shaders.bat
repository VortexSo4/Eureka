@echo off
REM ============================================================
REM  compile_shaders.bat
REM  Запускать из папки Assets\Shaders\Vulkan\
REM ============================================================

echo [EurekaSharp] Компиляция шейдеров в SPIR-V...

glslangValidator -V anim_compute.comp  -o anim_compute.spv
if %ERRORLEVEL% NEQ 0 ( echo ОШИБКА: anim_compute.comp & exit /b 1 )

glslangValidator -V morph_compute.comp -o morph_compute.spv
if %ERRORLEVEL% NEQ 0 ( echo ОШИБКА: morph_compute.comp & exit /b 1 )

glslangValidator -V render.vert        -o render.vert.spv
if %ERRORLEVEL% NEQ 0 ( echo ОШИБКА: render.vert & exit /b 1 )

glslangValidator -V render.frag        -o render.frag.spv
if %ERRORLEVEL% NEQ 0 ( echo ОШИБКА: render.frag & exit /b 1 )

echo [EurekaSharp] Все шейдеры скомпилированы успешно.
