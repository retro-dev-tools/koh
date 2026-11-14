use std::{
    collections::HashMap,
    time::{SystemTime, UNIX_EPOCH},
};

pub type Values = (HashMap<String, i32>, HashMap<String, String>);

pub fn values() -> Result<Values, String> {
    let seconds = match std::env::var("SOURCE_DATE_EPOCH") {
        Ok(value) => parse_epoch(&value)?,
        Err(std::env::VarError::NotPresent) => SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .map_err(|error| format!("Cannot determine current UTC time: {error}"))?
            .as_secs(),
        Err(std::env::VarError::NotUnicode(_)) => {
            return Err("SOURCE_DATE_EPOCH is not Unicode".into());
        }
    };
    at_epoch(seconds)
}

fn parse_epoch(value: &str) -> Result<u64, String> {
    if value.is_empty() || !value.bytes().all(|byte| byte.is_ascii_digit()) {
        return Err("SOURCE_DATE_EPOCH must be a nonnegative decimal UNIX timestamp".into());
    }
    value
        .parse()
        .map_err(|_| "SOURCE_DATE_EPOCH exceeds the supported timestamp range".into())
}

fn at_epoch(seconds: u64) -> Result<Values, String> {
    if seconds > 253_402_300_799 {
        return Err("UTC timestamp exceeds ISO 8601 year 9999".into());
    }
    let days = (seconds / 86_400) as i64;
    let (year, month, day) = civil_date(days);
    let hour = (seconds % 86_400 / 3_600) as i32;
    let minute = (seconds % 3_600 / 60) as i32;
    let second = (seconds % 60) as i32;
    let numbers = HashMap::from([
        ("__RGBDS_MAJOR__".into(), 1),
        ("__RGBDS_MINOR__".into(), 0),
        ("__RGBDS_PATCH__".into(), 1),
        ("__UTC_YEAR__".into(), year),
        ("__UTC_MONTH__".into(), month),
        ("__UTC_DAY__".into(), day),
        ("__UTC_HOUR__".into(), hour),
        ("__UTC_MINUTE__".into(), minute),
        ("__UTC_SECOND__".into(), second),
        ("__UTC_WEEKDAY__".into(), ((days + 4) % 7) as i32),
    ]);
    let strings = HashMap::from([
        ("__RGBDS_VERSION__".into(), "v1.0.1".into()),
        (
            "__ISO_8601_UTC__".into(),
            format!("{year:04}-{month:02}-{day:02}T{hour:02}:{minute:02}:{second:02}Z"),
        ),
    ]);
    Ok((numbers, strings))
}

fn civil_date(days: i64) -> (i32, i32, i32) {
    let shifted = days + 719_468;
    let era = shifted / 146_097;
    let day_of_era = shifted % 146_097;
    let year_of_era =
        (day_of_era - day_of_era / 1_460 + day_of_era / 36_524 - day_of_era / 146_096) / 365;
    let mut year = year_of_era + era * 400;
    let day_of_year = day_of_era - (365 * year_of_era + year_of_era / 4 - year_of_era / 100);
    let march_month = (5 * day_of_year + 2) / 153;
    let day = day_of_year - (153 * march_month + 2) / 5 + 1;
    let month = march_month + if march_month < 10 { 3 } else { -9 };
    year += i64::from(month <= 2);
    (year as i32, month as i32, day as i32)
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn epoch_and_version() {
        let (numbers, strings) = at_epoch(0).unwrap();
        assert_eq!(numbers["__UTC_YEAR__"], 1970);
        assert_eq!(numbers["__UTC_MONTH__"], 1);
        assert_eq!(numbers["__UTC_DAY__"], 1);
        assert_eq!(numbers["__UTC_WEEKDAY__"], 4);
        assert_eq!(numbers["__RGBDS_PATCH__"], 1);
        assert!(!numbers.contains_key("__RGBDS_RC__"));
        assert_eq!(strings["__RGBDS_VERSION__"], "v1.0.1");
        assert_eq!(strings["__ISO_8601_UTC__"], "1970-01-01T00:00:00Z");
    }
    #[test]
    fn leap_century_and_year_boundaries() {
        for (seconds, date) in [
            (951_782_400, "2000-02-29T00:00:00Z"),
            (951_868_800, "2000-03-01T00:00:00Z"),
            (1_609_459_199, "2020-12-31T23:59:59Z"),
            (1_609_459_200, "2021-01-01T00:00:00Z"),
            (4_107_542_400, "2100-03-01T00:00:00Z"),
            (253_402_300_799, "9999-12-31T23:59:59Z"),
        ] {
            assert_eq!(at_epoch(seconds).unwrap().1["__ISO_8601_UTC__"], date);
        }
        assert!(at_epoch(253_402_300_800).is_err());
    }
    #[test]
    fn source_epoch_validation() {
        assert_eq!(parse_epoch("0").unwrap(), 0);
        assert_eq!(parse_epoch("000123").unwrap(), 123);
        for text in [
            "",
            "-1",
            "+1",
            " 1",
            "1 ",
            "1.0",
            "1x",
            "１２",
            "18446744073709551616",
        ] {
            assert!(parse_epoch(text).is_err(), "{text}");
        }
    }
}
