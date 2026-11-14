pub mod analysis;
mod assembler;
mod builtins;
mod context;
mod draft;
pub mod encode;
pub mod expr;
mod lower;
pub mod model;
pub mod object;
mod resolver;
pub mod service;
mod source;

pub use assembler::assemble_with_overlays;
pub use assembler::{
    AssemblyReport, Diagnostic, assemble, assemble_report, assemble_with_overlays_report,
};
