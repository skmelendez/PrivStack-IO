//! S3 upload/download operations using STS credentials.
//!
//! Handles encrypted batch uploads and downloads. Credentials are provided
//! by the credential manager and refreshed transparently.

use crate::error::{CloudError, CloudResult};
use crate::types::StsCredentials;
use aws_sdk_s3::Client as S3Client;
use aws_sdk_s3::primitives::ByteStream;
use tracing::debug;

/// S3 transport for uploading/downloading encrypted data.
pub struct S3Transport {
    bucket: String,
    region: String,
    endpoint_override: Option<String>,
}

impl S3Transport {
    pub fn new(bucket: String, region: String, endpoint_override: Option<String>) -> Self {
        Self {
            bucket,
            region,
            endpoint_override,
        }
    }

    /// Builds an S3 client from STS credentials.
    async fn build_client(&self, creds: &StsCredentials) -> CloudResult<S3Client> {
        let credentials = aws_credential_types::Credentials::new(
            &creds.access_key_id,
            &creds.secret_access_key,
            Some(creds.session_token.clone()),
            None,
            "privstack-sts",
        );

        let mut config_builder = aws_sdk_s3::Config::builder()
            .region(aws_types::region::Region::new(self.region.clone()))
            .credentials_provider(credentials)
            .behavior_version_latest();

        if let Some(ref endpoint) = self.endpoint_override {
            config_builder = config_builder
                .endpoint_url(endpoint)
                .force_path_style(true);
        }

        Ok(S3Client::from_conf(config_builder.build()))
    }

    /// Uploads encrypted data to S3.
    pub async fn upload(
        &self,
        creds: &StsCredentials,
        key: &str,
        data: Vec<u8>,
    ) -> CloudResult<()> {
        if creds.is_expired() {
            return Err(CloudError::CredentialExpired);
        }

        let client = self.build_client(creds).await?;
        let size = data.len();

        client
            .put_object()
            .bucket(&self.bucket)
            .key(key)
            .body(ByteStream::from(data))
            .send()
            .await
            .map_err(|e| CloudError::S3(format!("upload failed for {key}: {e}")))?;

        debug!("uploaded {size} bytes to s3://{}/{key}", self.bucket);
        Ok(())
    }

    /// Downloads encrypted data from S3.
    pub async fn download(&self, creds: &StsCredentials, key: &str) -> CloudResult<Vec<u8>> {
        if creds.is_expired() {
            return Err(CloudError::CredentialExpired);
        }

        let client = self.build_client(creds).await?;

        let resp = client
            .get_object()
            .bucket(&self.bucket)
            .key(key)
            .send()
            .await
            .map_err(|e| CloudError::S3(format!("download failed for {key}: {e}")))?;

        let body = resp
            .body
            .collect()
            .await
            .map_err(|e| CloudError::S3(format!("failed to read body for {key}: {e}")))?;

        let bytes = body.into_bytes().to_vec();
        debug!(
            "downloaded {} bytes from s3://{}/{key}",
            bytes.len(),
            self.bucket
        );
        Ok(bytes)
    }

    /// Checks if an object exists in S3 (HEAD request).
    pub async fn exists(&self, creds: &StsCredentials, key: &str) -> CloudResult<bool> {
        if creds.is_expired() {
            return Err(CloudError::CredentialExpired);
        }

        let client = self.build_client(creds).await?;

        match client
            .head_object()
            .bucket(&self.bucket)
            .key(key)
            .send()
            .await
        {
            Ok(_) => Ok(true),
            Err(e) => {
                let service_err = e.into_service_error();
                if service_err.is_not_found() {
                    Ok(false)
                } else {
                    Err(CloudError::S3(format!("head object failed for {key}: {service_err}")))
                }
            }
        }
    }

    /// Lists objects under a prefix.
    pub async fn list_keys(
        &self,
        creds: &StsCredentials,
        prefix: &str,
    ) -> CloudResult<Vec<String>> {
        if creds.is_expired() {
            return Err(CloudError::CredentialExpired);
        }

        let client = self.build_client(creds).await?;

        let resp = client
            .list_objects_v2()
            .bucket(&self.bucket)
            .prefix(prefix)
            .send()
            .await
            .map_err(|e| CloudError::S3(format!("list failed for prefix {prefix}: {e}")))?;

        let keys = resp
            .contents()
            .iter()
            .filter_map(|obj| obj.key().map(|k| k.to_string()))
            .collect();

        Ok(keys)
    }
}
