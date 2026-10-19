# ADR 0013 — owned synchronous log file и in-place rename

Class: файловые операции existing logger внутри проверенного LogDirectory;
произвольный std::fs::File, чужой source и overlapped handle вне класса.
Inv: LogFile создаётся только синхронным checked open, удерживает Root/owner/pins,
replace принимает только тот же Root; native lifecycle + E12 regression suite.
N+1: preference/rotation используют единственный replace; новые consumers получают
тот же typed file, без добавления path-based rename/fallback или unsafe вне native.

## Evidence и решение

Первый полный MSVC runtime дошёл до Host console PASS, затем E12 tests получили
sharing violations. Native regression локализовал отказ на replace existing target.
Win32 full-path SetFileInformationByHandle требует destination resolution через
parent directory; его открытия конфликтуют с existing strong ancestor pins.
Попытка RootDirectory + component дала ERROR_INVALID_PARAMETER в Win32 wrapper.
Ослаблять pins/share flags или временно отпускать их ради rename нельзя.

Выбран documented NtSetInformationFile(FileRenameInformation), RootDirectory=NULL,
FileName=один валидированный компонент. Это kernel in-place rename в том же parent,
не разрешение относительно CWD. Source file остаётся тем же проверенным handle.
Никакого full-path rename, retry, POSIX bypass, игнорирования readonly или force нет.
FileDispositionInfo clear по-прежнему использует прежний checked Win32 handle.

## Safe boundary и lifetime

LogFile — private File + Arc<Root>, без конструктора из произвольного File/raw handle,
без Deref/AsRawHandle/try_clone наружу. Read/Write/Seek/metadata/resize/sync делегируются
тому же File. Root содержит прежние pins и exclusive rust-logger.lock. Они живут до
закрытия последнего LogFile, даже если внешний LogDirectory уже dropped.
Arc identity проверяет source owner перед rename; cross-directory source отказан.

NtSetInformationFile требует сохранения borrowed buffers до завершения I/O. Поэтому
обычный File более не является допустимым аргументом: только LogDirectory::open_file
создаёт LogFile, всегда без FILE_FLAG_OVERLAPPED. Для synchronous handle вызов
завершается до возврата. IO_STATUS_BLOCK и repr(C) rename buffer принадлежат stack;
layout checked against windows-sys FILE_RENAME_INFORMATION. NTSTATUS ошибки
переводятся через RtlNtStatusToDosError, ошибку не подменяет ложный успех.

FFI остаётся исключительно в icem-windows-native/log_directory.rs, согласно ADR 0001.
Wdk_Storage_FileSystem — один feature уже pinned windows-sys 0.61.2, MIT OR Apache-2.0.
Он предоставляет user-mode ntdll.dll import NtSetInformationFile и SDK types; kernel
module/driver, SDK runner, dependency/version/lockfile, упаковка не добавляются.
Новый unsafe ограничен вызовом над private synchronous file и конвертацией NTSTATUS;
workspace forbid и native deny/SAFETY-comment guards прежние. Application logger safe.

Windows tests проверяют full sequence с sibling root, foreign source rejection,
owner retention/release, read-live, replace existing twice, reopen, delete и cleanup.
Полный E12 suite проверяет ON/OFF persistence, clear, partial failure, locks, readonly,
reparse/hardlink/path bounds, rotation/disk budget, owner independence и retention.
Hardware/OS driver hangs и power-loss durability не объявляются пройденными по CI.

Primary API:
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/nf-ntifs-ntsetinformationfile
- https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/ntifs/ns-ntifs-_file_rename_information
