//! Bounded image-only PDF. The caller publishes the finished temporary file.
use flate2::{write::ZlibEncoder, Compression};
use image::{imageops::FilterType, RgbImage};
use sha2::{Digest, Sha256};
use std::fs::{File, OpenOptions};
use std::io::{BufWriter, Seek, Write};
use std::path::Path;

const TOTAL_BYTES: u64 = 256 * 1024 * 1024;
const MAX_SLIDES: usize = 1000;

#[derive(Debug, thiserror::Error)]
pub enum Error {
    #[error("PPT 导出超过图片数量或数据大小限制")]
    Limit,
    #[error("PPT 图片无效，或导出尚无有效页面")]
    Image,
    #[error("无法写入完整的 PPT PDF")]
    Io(#[from] std::io::Error),
}

struct Fingerprint {
    exact: [u8; 32],
    edges: [u16; 16],
    ratio: f64,
    preview: RgbImage,
}
impl Fingerprint {
    fn new(rgb: &RgbImage) -> Self {
        let preview = image::imageops::resize(rgb, 640, 360, FilterType::Lanczos3);
        let small = image::imageops::resize(
            &image::imageops::grayscale(&preview),
            17,
            16,
            FilterType::Lanczos3,
        );
        let edges = std::array::from_fn(|y| {
            (0..16).fold(0, |bits, x| {
                bits << 1
                    | u16::from(
                        small.get_pixel(x, y as u32)[0] > small.get_pixel(x + 1, y as u32)[0],
                    )
            })
        });
        let mut digest = Sha256::new();
        digest.update(rgb.width().to_be_bytes());
        digest.update(rgb.height().to_be_bytes());
        digest.update(rgb.as_raw());
        Self {
            exact: digest.finalize().into(),
            edges,
            ratio: f64::from(rgb.width()) / f64::from(rgb.height()),
            preview,
        }
    }

    fn same(&self, other: &Self) -> bool {
        if self.exact == other.exact {
            return true;
        }
        if (self.ratio - other.ratio).abs() > 0.005
            || self
                .edges
                .iter()
                .zip(other.edges)
                .map(|(a, b)| (a ^ b).count_ones())
                .sum::<u32>()
                > 12
        {
            return false;
        }
        let mut total = [0_u32; 3];
        let mut blocks = [0_u8; 40 * 23];
        for (i, (a, b)) in self
            .preview
            .pixels()
            .zip(other.preview.pixels())
            .enumerate()
        {
            let delta = std::array::from_fn::<_, 3, _>(|c| a[c].abs_diff(b[c]));
            for c in 0..3 {
                total[c] += u32::from(delta[c]);
            }
            if delta.into_iter().any(|d| d > 24) {
                let block = (i / 640) * 23 / 360 * 40 + (i % 640) / 16;
                blocks[block] += 1;
                // Three changed thumbnail pixels can already represent a tiny annotation.
                if blocks[block] >= 3 {
                    return false;
                }
            }
        }
        total.into_iter().all(|sum| sum <= 576_000)
    }
}

struct Page {
    fingerprint: Fingerprint,
    width: u32,
    height: u32,
    times: Vec<u64>,
}

pub struct SlidePdf {
    output: BufWriter<File>,
    offsets: Vec<u64>,
    pages: Vec<Page>,
    encoded: u64,
    decoded: u64,
    count: usize,
}
impl SlidePdf {
    pub fn create(path: &Path, snapshot: &str) -> Result<Self, Error> {
        if snapshot.len() != 64 || !snapshot.bytes().all(|b| b.is_ascii_hexdigit()) {
            return Err(Error::Image);
        }
        let mut output =
            BufWriter::new(OpenOptions::new().write(true).create_new(true).open(path)?);
        writeln!(output, "%PDF-1.4\n%BUAA-Slides {snapshot}")?;
        Ok(Self {
            output,
            offsets: vec![0; 4],
            pages: vec![],
            encoded: 0,
            decoded: 0,
            count: 0,
        })
    }

    fn object(&mut self, id: usize) -> Result<(), Error> {
        self.offsets.resize(self.offsets.len().max(id + 1), 0);
        self.offsets[id] = self.output.stream_position()?;
        writeln!(self.output, "{id} 0 obj")?;
        Ok(())
    }

    pub fn add(&mut self, bytes: &[u8], seconds: u64) -> Result<(), Error> {
        self.count += 1;
        self.encoded += bytes.len() as u64;
        if self.count > MAX_SLIDES || self.encoded > TOTAL_BYTES {
            return Err(Error::Limit);
        }
        let (image, _, original_bytes) =
            crate::slide_image::decode(bytes, true).map_err(|_| Error::Image)?;
        let rgb = image.into_rgb8();
        let fingerprint = Fingerprint::new(&rgb);
        if let Some(page) = self
            .pages
            .iter_mut()
            .find(|page| page.fingerprint.same(&fingerprint))
        {
            page.times.push(seconds);
            return Ok(());
        }
        self.decoded += original_bytes
            .max(rgb.as_raw().len() as u64)
            .max(fingerprint.preview.as_raw().len() as u64);
        if self.decoded > TOTAL_BYTES {
            return Err(Error::Limit);
        }
        let (width, height) = rgb.dimensions();
        let id = 4 + self.pages.len() * 4;
        self.object(id)?;
        writeln!(self.output, "<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode /Length {} 0 R >>\nstream", id + 1)?;
        let start = self.output.stream_position()?;
        let mut encoder = ZlibEncoder::new(&mut self.output, Compression::fast());
        encoder.write_all(rgb.as_raw())?;
        encoder.finish()?;
        let length = self.output.stream_position()? - start;
        writeln!(self.output, "\nendstream\nendobj")?;
        self.object(id + 1)?;
        writeln!(self.output, "{length}\nendobj")?;
        self.pages.push(Page {
            fingerprint,
            width,
            height,
            times: vec![seconds],
        });
        Ok(())
    }

    pub fn finish(&mut self) -> Result<usize, Error> {
        let count = self.pages.len();
        if count == 0 {
            return Err(Error::Image);
        }
        for index in 0..count {
            let page = &self.pages[index];
            let height = 960.0 * f64::from(page.height) / f64::from(page.width);
            let clock = |seconds: u64| {
                format!(
                    "{:02}:{:02}:{:02}",
                    seconds / 3600,
                    seconds / 60 % 60,
                    seconds % 60
                )
            };
            let mut times = page
                .times
                .iter()
                .take(8)
                .map(|&time| clock(time))
                .collect::<Vec<_>>()
                .join(", ");
            if page.times.len() > 8 {
                times.push_str(&format!(" +{}", page.times.len() - 8));
            }
            let content = format!("q 960 0 0 {height:.4} 0 22 cm /Slide Do Q\nBT /F1 9 Tf 0.45 g 12 7 Td ({} / {count}    {times}) Tj ET\n", index + 1);
            let id = 4 + index * 4;
            self.object(id + 2)?;
            writeln!(self.output, "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 960 {:.4}] /Resources << /XObject << /Slide {id} 0 R >> /Font << /F1 3 0 R >> >> /Contents {} 0 R >>\nendobj", height + 22.0, id + 3)?;
            self.object(id + 3)?;
            write!(
                self.output,
                "<< /Length {} >>\nstream\n{content}endstream\nendobj\n",
                content.len()
            )?;
        }
        self.object(1)?;
        writeln!(self.output, "<< /Type /Catalog /Pages 2 0 R >>\nendobj")?;
        self.object(2)?;
        let kids = (0..count)
            .map(|i| format!("{} 0 R", 6 + i * 4))
            .collect::<Vec<_>>()
            .join(" ");
        writeln!(
            self.output,
            "<< /Type /Pages /Kids [{kids}] /Count {count} >>\nendobj"
        )?;
        self.object(3)?;
        writeln!(
            self.output,
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>\nendobj"
        )?;
        let xref = self.output.stream_position()?;
        let size = self.offsets.len();
        writeln!(self.output, "xref\n0 {size}\n0000000000 65535 f ")?;
        for offset in &self.offsets[1..] {
            writeln!(self.output, "{offset:010} 00000 n ")?;
        }
        writeln!(
            self.output,
            "trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF"
        )?;
        self.output.flush()?;
        self.output.get_ref().sync_all()?;
        Ok(count)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::io::{Cursor, Read};

    fn png(image: &RgbImage) -> Vec<u8> {
        let mut out = Cursor::new(Vec::new());
        image.write_to(&mut out, image::ImageFormat::Png).unwrap();
        out.into_inner()
    }

    #[test]
    fn preserves_pixels_and_local_annotations_and_merges_nonadjacent_duplicate() {
        let base = RgbImage::from_pixel(640, 360, image::Rgb([240, 240, 240]));
        let mut annotated = base.clone();
        for y in 13..17 {
            for x in 11..15 {
                annotated.put_pixel(x, y, image::Rgb([220, 20, 70]));
            }
        }
        let path = std::env::temp_dir().join(format!("buaa-pdf-test-{}.pdf", std::process::id()));
        let mut pdf = SlidePdf::create(&path, &"0".repeat(64)).unwrap();
        let bytes = png(&base);
        pdf.add(&bytes, 1).unwrap();
        pdf.add(&png(&annotated), 6).unwrap();
        pdf.add(&bytes, 11).unwrap();
        assert_eq!(pdf.finish().unwrap(), 2);
        drop(pdf);
        let result = std::fs::read(&path).unwrap();
        std::fs::remove_file(path).unwrap();
        let marker = b"stream\n";
        let first = result
            .windows(marker.len())
            .position(|chunk| chunk == marker)
            .unwrap()
            + marker.len();
        let mut raw = Vec::new();
        flate2::read::ZlibDecoder::new(&result[first..])
            .read_to_end(&mut raw)
            .unwrap();
        assert_eq!(raw, *base.as_raw());
        assert!(result.windows(18).any(|s| s == b"00:00:01, 00:00:11"));
        assert!(result.ends_with(b"%%EOF\n"));
    }
}
