//! Integration tests for S3Transport against real MinIO.
//!
//! Requires: `docker compose -f docker-compose.test.yml up -d`

mod support;

use pretty_assertions::assert_eq;
use privstack_cloud::CloudError;
use serial_test::serial;

#[tokio::test]
#[serial]
async fn upload_download_roundtrip() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();
    let key = format!("{prefix}/roundtrip.bin");

    let payload = b"hello integration test";
    transport.upload(&creds, &key, payload.to_vec()).await.unwrap();

    let downloaded = transport.download(&creds, &key).await.unwrap();
    assert_eq!(downloaded, payload.to_vec());
}

#[tokio::test]
#[serial]
async fn exists_returns_true_after_upload_false_for_missing() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    let key = format!("{prefix}/exists-check.bin");
    assert!(!transport.exists(&creds, &key).await.unwrap());

    transport.upload(&creds, &key, b"data".to_vec()).await.unwrap();
    assert!(transport.exists(&creds, &key).await.unwrap());
}

#[tokio::test]
#[serial]
async fn list_keys_finds_uploaded_objects() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    transport
        .upload(&creds, &format!("{prefix}/a.bin"), b"a".to_vec())
        .await
        .unwrap();
    transport
        .upload(&creds, &format!("{prefix}/b.bin"), b"b".to_vec())
        .await
        .unwrap();

    let keys = transport.list_keys(&creds, &prefix).await.unwrap();
    assert_eq!(keys.len(), 2);
    assert!(keys.contains(&format!("{prefix}/a.bin")));
    assert!(keys.contains(&format!("{prefix}/b.bin")));
}

#[tokio::test]
#[serial]
async fn list_keys_empty_for_unused_prefix() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();

    let keys = transport.list_keys(&creds, &prefix).await.unwrap();
    assert!(keys.is_empty());
}

#[tokio::test]
#[serial]
async fn expired_creds_rejected_before_network_call() {
    let transport = support::test_transport();
    let creds = support::expired_minio_creds();
    let key = "should-not-reach-minio.bin";

    let err = transport.upload(&creds, key, b"data".to_vec()).await.unwrap_err();
    assert!(matches!(err, CloudError::CredentialExpired));

    let err = transport.download(&creds, key).await.unwrap_err();
    assert!(matches!(err, CloudError::CredentialExpired));

    let err = transport.exists(&creds, key).await.unwrap_err();
    assert!(matches!(err, CloudError::CredentialExpired));

    let err = transport.list_keys(&creds, "prefix/").await.unwrap_err();
    assert!(matches!(err, CloudError::CredentialExpired));
}

#[tokio::test]
#[serial]
async fn download_nonexistent_key_returns_s3_error() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();
    let key = format!("{prefix}/does-not-exist.bin");

    let err = transport.download(&creds, &key).await.unwrap_err();
    assert!(matches!(err, CloudError::S3(_)));
}

#[tokio::test]
#[serial]
async fn overwrite_returns_latest_data() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();
    let key = format!("{prefix}/overwrite.bin");

    transport.upload(&creds, &key, b"version-1".to_vec()).await.unwrap();
    transport.upload(&creds, &key, b"version-2".to_vec()).await.unwrap();

    let data = transport.download(&creds, &key).await.unwrap();
    assert_eq!(data, b"version-2");
}

#[tokio::test]
#[serial]
async fn large_object_upload_download() {
    let transport = support::test_transport();
    let creds = support::fake_minio_creds();
    let prefix = support::unique_prefix();
    let key = format!("{prefix}/large-5mb.bin");

    let payload: Vec<u8> = (0..5_000_000u32).map(|i| (i % 256) as u8).collect();
    transport.upload(&creds, &key, payload.clone()).await.unwrap();

    let downloaded = transport.download(&creds, &key).await.unwrap();
    assert_eq!(downloaded.len(), payload.len());
    assert_eq!(downloaded, payload);
}
