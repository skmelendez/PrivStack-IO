use privstack_cloud::CloudConfig;

#[test]
fn default_api_base_url() {
    let config = CloudConfig::default();
    assert_eq!(config.api_base_url, "https://api.privstack.io");
}

#[test]
fn default_s3_bucket() {
    let config = CloudConfig::default();
    assert_eq!(config.s3_bucket, "privstack-cloud");
}

#[test]
fn default_s3_region() {
    let config = CloudConfig::default();
    assert_eq!(config.s3_region, "us-east-1");
}

#[test]
fn default_no_endpoint_override() {
    let config = CloudConfig::default();
    assert!(config.s3_endpoint_override.is_none());
}

#[test]
fn default_credential_refresh_margin() {
    let config = CloudConfig::default();
    assert_eq!(config.credential_refresh_margin_secs, 300);
}

#[test]
fn default_poll_interval() {
    let config = CloudConfig::default();
    assert_eq!(config.poll_interval_secs, 30);
}

#[test]
fn serialization_roundtrip() {
    let config = CloudConfig::default();
    let json = serde_json::to_string(&config).unwrap();
    let deserialized: CloudConfig = serde_json::from_str(&json).unwrap();
    assert_eq!(deserialized.api_base_url, config.api_base_url);
    assert_eq!(deserialized.s3_bucket, config.s3_bucket);
    assert_eq!(deserialized.s3_region, config.s3_region);
    assert_eq!(deserialized.s3_endpoint_override, config.s3_endpoint_override);
    assert_eq!(deserialized.credential_refresh_margin_secs, config.credential_refresh_margin_secs);
    assert_eq!(deserialized.poll_interval_secs, config.poll_interval_secs);
}

#[test]
fn optional_endpoint_override_roundtrip() {
    let mut config = CloudConfig::default();
    config.s3_endpoint_override = Some("http://localhost:9000".to_string());
    let json = serde_json::to_string(&config).unwrap();
    let deserialized: CloudConfig = serde_json::from_str(&json).unwrap();
    assert_eq!(deserialized.s3_endpoint_override, Some("http://localhost:9000".to_string()));
}
