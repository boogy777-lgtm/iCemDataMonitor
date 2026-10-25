# Mosquitto 2.0.22 lib/CMakeLists.txt:63 and src/CMakeLists.txt:166 name
# OpenSSL::SSL unconditionally. WITH_TLS=OFF compiles out all TLS calls.
# Supply only the empty link interface; do not locate/link an OpenSSL library.
if(NOT DEFINED WITH_TLS OR WITH_TLS)
    message(FATAL_ERROR "The acceptance hook only supports explicit WITH_TLS=OFF")
endif()
add_library(OpenSSL::SSL INTERFACE IMPORTED GLOBAL)
