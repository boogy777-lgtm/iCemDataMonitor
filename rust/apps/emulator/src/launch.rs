//! First-token application modes, with existing explicit development switches.
#[derive(Debug, PartialEq, Eq)]
pub enum Mode {
    Ui { args: Vec<String>, default: bool },
    Cli { pipe: String, args: Vec<String> },
    SelfTest,
    LanDiag(Option<String>),
    Help,
}
pub fn parse(args: Vec<String>) -> Mode {
    let first = args.first().map(|s| s.trim()).unwrap_or("");
    if super::selftest::requested(Some(first)) {
        return Mode::SelfTest;
    }
    let mode = first.to_ascii_lowercase();
    match mode.as_str() {
        "--cli" => Mode::Cli {
            pipe: icem_emulation::CONTROL_PIPE.into(),
            args: args[1..].to_vec(),
        },
        "--landiag" => Mode::LanDiag(args.get(1).cloned()),
        "--help" | "-h" | "/?" => Mode::Help,
        "--pipe" if args.get(2).is_some_and(|s| s == "--cli") => Mode::Cli {
            pipe: args[1].clone(),
            args: args[3..].to_vec(),
        },
        "--config" | "--templates" | "--duration-ms" | "--pipe" | "--desktop" => Mode::Ui {
            args,
            default: false,
        },
        _ => Mode::Ui {
            args: Vec::new(),
            default: true,
        },
    }
}
pub const USAGE: &str = "iCemEmulator — standalone iCem emulator (COM FLECS + LAN ACE), built-in UI and CLI.\n  (no args)              open application window\n  --cli <command>        command to running instance, or commands via redirected stdin\n  --selftest             wire-format self tests\n  --landiag <adapter>    LAN loopback diagnostic\n  --help, -h, /?         this help\nDevelopment: --config PATH --templates DIR [--pipe NAME] [--duration-ms N] [--desktop]";
#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn first_token_and_development_modes() {
        for key in ["--help", " -H ", "/?"] {
            assert_eq!(parse(vec![key.into(), "ignored".into()]), Mode::Help);
        }
        for args in [vec![], vec!["unknown".into(), "--cli".into()]] {
            assert_eq!(
                parse(args),
                Mode::Ui {
                    args: vec![],
                    default: true
                }
            );
        }
        assert_eq!(
            parse(vec![" --SELFTEST ".into(), "ignored".into()]),
            Mode::SelfTest
        );
        assert_eq!(
            parse(vec![" --LaNdIaG ".into(), " ".into(), "ignored".into()]),
            Mode::LanDiag(Some(" ".into()))
        );
        assert_eq!(
            parse(vec![
                "--pipe".into(),
                "TestCtl".into(),
                "--cli".into(),
                "status".into()
            ]),
            Mode::Cli {
                pipe: "TestCtl".into(),
                args: vec!["status".into()]
            }
        );
        assert!(matches!(
            parse(vec!["--config".into(), "a.xml".into()]),
            Mode::Ui { default: false, .. }
        ));
    }
}
