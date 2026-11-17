//! Validate original slide bytes without resizing or lossy re-encoding.
use image::{DynamicImage, ImageDecoder, ImageFormat, ImageReader};
use serde::Serialize;
use sha2::{Digest, Sha256};
use std::io::Cursor;

pub const MAX_BYTES: usize = 12 * 1024 * 1024;
pub const MAX_PIXELS: u64 = 20_000_000;
pub const MAX_DECODED: u64 = 256 * 1024 * 1024;

#[derive(Debug, thiserror::Error)]
#[error("PPT 图片格式、尺寸或内容无效")]
pub struct ImageError;
#[derive(Debug, Serialize)]
pub struct ImageInfo {
    pub width: u32,
    pub height: u32,
    pub decoded_bytes: u64,
    pub bytes: usize,
    pub mime: &'static str,
    pub digest: String,
}
pub fn inspect(bytes: &[u8]) -> Result<ImageInfo, ImageError> {
    let (image, mime, decoded_bytes) = decode(bytes, false)?;
    Ok(ImageInfo {
        width: image.width(),
        height: image.height(),
        decoded_bytes,
        bytes: bytes.len(),
        mime,
        digest: format!("{:x}", Sha256::digest(bytes)),
    })
}

pub(crate) fn decode(
    bytes: &[u8],
    orient: bool,
) -> Result<(DynamicImage, &'static str, u64), ImageError> {
    if bytes.is_empty() || bytes.len() > MAX_BYTES {
        return Err(ImageError);
    }
    let format = image::guess_format(bytes).map_err(|_| ImageError)?;
    let mime = match format {
        ImageFormat::Png => "image/png",
        ImageFormat::Jpeg => "image/jpeg",
        ImageFormat::WebP => "image/webp",
        _ => return Err(ImageError),
    };
    let mut limits = image::Limits::default();
    limits.max_image_width = Some(MAX_PIXELS as u32);
    limits.max_image_height = Some(MAX_PIXELS as u32);
    limits.max_alloc = Some(MAX_DECODED);
    let mut reader = ImageReader::with_format(Cursor::new(bytes), format);
    reader.limits(limits);
    let mut decoder = reader.into_decoder().map_err(|_| ImageError)?;
    let (width, height) = decoder.dimensions();
    let decoded_bytes = decoder.total_bytes();
    if width == 0
        || height == 0
        || u64::from(width) * u64::from(height) > MAX_PIXELS
        || decoded_bytes > MAX_DECODED
    {
        return Err(ImageError);
    }
    // Header inspection alone is insufficient: truncated/corrupt data must fail.
    let orientation = if orient {
        Some(decoder.orientation().map_err(|_| ImageError)?)
    } else {
        None
    };
    let mut decoded = DynamicImage::from_decoder(decoder).map_err(|_| ImageError)?;
    if let Some(orientation) = orientation {
        decoded.apply_orientation(orientation);
    }
    Ok((decoded, mime, decoded_bytes))
}
#[cfg(test)]
mod tests {
    use super::*;
    fn encoded(format: ImageFormat) -> Vec<u8> {
        let image = image::DynamicImage::ImageRgb8(image::RgbImage::from_pixel(
            3,
            2,
            image::Rgb([240, 250, 255]),
        ));
        let mut out = Cursor::new(Vec::new());
        image.write_to(&mut out, format).unwrap();
        out.into_inner()
    }
    #[test]
    fn accepts_original_png_jpeg_webp_and_fingerprints_original_bytes() {
        for format in [ImageFormat::Png, ImageFormat::Jpeg, ImageFormat::WebP] {
            let bytes = encoded(format);
            let info = inspect(&bytes).unwrap();
            assert_eq!((info.width, info.height), (3, 2));
            assert_eq!(info.bytes, bytes.len());
            assert_eq!(info.digest, format!("{:x}", Sha256::digest(&bytes)));
        }
    }
    #[test]
    fn rejects_a_valid_compressed_image_above_pixel_budget() {
        let image = image::DynamicImage::ImageLuma8(image::GrayImage::new(5001, 4000));
        let mut out = Cursor::new(Vec::new());
        image.write_to(&mut out, ImageFormat::Png).unwrap();
        assert!(out.get_ref().len() < MAX_BYTES);
        assert!(inspect(out.get_ref()).is_err());
    }
    #[test]
    fn rejects_non_images_corruption_and_byte_limit() {
        for bytes in [
            vec![],
            b"<svg xmlns='http://www.w3.org/2000/svg'/>".to_vec(),
            vec![0; MAX_BYTES + 1],
        ] {
            assert!(inspect(&bytes).is_err());
        }
        let mut bytes = encoded(ImageFormat::Png);
        bytes.truncate(bytes.len() / 2);
        assert!(inspect(&bytes).is_err());
        let mut bytes = encoded(ImageFormat::Png);
        let last = bytes.len() / 2;
        bytes[last] ^= 255;
        assert!(inspect(&bytes).is_err());
    }
}
