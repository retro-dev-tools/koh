use crate::{BinaryOp, UnaryOp};
pub fn evaluate_binary(op: BinaryOp, a: i32, b: i32) -> Result<i32, String> {
    use BinaryOp::*;
    Ok(match op {
        Add => a.wrapping_add(b),
        Subtract => a.wrapping_sub(b),
        Multiply => a.wrapping_mul(b),
        Divide | Modulo => {
            if b == 0 {
                return Err("Division by zero".into());
            }
            let a = i64::from(a);
            let b = i64::from(b);
            let mut q = a / b;
            let mut r = a % b;
            if r != 0 && (r < 0) != (b < 0) {
                q -= 1;
                r += b;
            }
            if op == Divide { q as i32 } else { r as i32 }
        }
        Power => {
            if b < 0 {
                return Err("Negative exponent".into());
            }
            a.wrapping_pow(b as u32)
        }
        BitOr => a | b,
        BitAnd => a & b,
        BitXor => a ^ b,
        LogicalAnd => (a != 0 && b != 0) as i32,
        LogicalOr => (a != 0 || b != 0) as i32,
        Equal => (a == b) as i32,
        NotEqual => (a != b) as i32,
        Greater => (a > b) as i32,
        Less => (a < b) as i32,
        GreaterEqual => (a >= b) as i32,
        LessEqual => (a <= b) as i32,
        ShiftLeft | ShiftRight | UnsignedShiftRight => shift(op, a, b),
    })
}
fn shift(op: BinaryOp, a: i32, b: i32) -> i32 {
    let left = matches!(op, BinaryOp::ShiftLeft) ^ (b < 0);
    let n = b.unsigned_abs();
    if n >= 32 {
        if !left && op != BinaryOp::UnsignedShiftRight && a < 0 {
            -1
        } else {
            0
        }
    } else if left {
        a.wrapping_shl(n)
    } else if op == BinaryOp::UnsignedShiftRight {
        ((a as u32) >> n) as i32
    } else {
        a >> n
    }
}

pub fn evaluate_unary(op: UnaryOp, v: i32) -> i32 {
    match op {
        UnaryOp::Negate => v.wrapping_neg(),
        UnaryOp::BitNot => !v,
        UnaryOp::LogicalNot => (v == 0) as i32,
        UnaryOp::High => (v >> 8) & 255,
        UnaryOp::Low => v & 255,
        UnaryOp::BitWidth => (32 - (v as u32).leading_zeros()) as i32,
        UnaryOp::TrailingZeros => v.trailing_zeros() as i32,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn numeric_operations_keep_sm83_expression_rules() {
        assert_eq!(evaluate_binary(BinaryOp::Divide, -7, 3).unwrap(), -3);
        assert_eq!(evaluate_binary(BinaryOp::Modulo, -7, 3).unwrap(), 2);
        assert_eq!(
            evaluate_binary(BinaryOp::Add, i32::MAX, 1).unwrap(),
            i32::MIN
        );
        assert_eq!(
            evaluate_binary(BinaryOp::UnsignedShiftRight, -1, 32).unwrap(),
            0
        );
        assert_eq!(evaluate_binary(BinaryOp::ShiftLeft, -1, -32).unwrap(), -1);
        assert!(evaluate_binary(BinaryOp::Divide, 1, 0).is_err());
        assert!(evaluate_binary(BinaryOp::Power, 2, -1).is_err());
        assert_eq!(evaluate_unary(UnaryOp::TrailingZeros, 0), 32);
        assert_eq!(evaluate_unary(UnaryOp::High, 0x1234), 0x12);
    }
}
