#![forbid(unsafe_code)]
//! M0 composition placeholder. This binary MUST NOT replace the legacy executable.
use std::process::ExitCode;

fn main() -> ExitCode {
    eprintln!(
        "ICEM shell: migration skeleton only; Windows runtime is not implemented. See docs/MIGRATION_PLAN.ru.md."
    );
    ExitCode::from(78)
}
