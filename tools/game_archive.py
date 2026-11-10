"""Read individual files out of Elden Ring's encrypted archives (Data*.bhd/.bdt, DLC.bhd/.bdt).

Read-only: it never writes to the game folder. The archive header keys are read from the
randomizer's own exe, which carries them to do the same job.
"""
import re
import struct
from pathlib import Path

from cryptography.hazmat.primitives.ciphers import Cipher, algorithms, modes
from cryptography.hazmat.primitives.serialization import load_pem_public_key

ARCHIVE_NAMES = ["Data0", "Data1", "Data2", "Data3", "DLC"]
HASH_PRIME = 0x85
HASH_MASK = (1 << 64) - 1
FILE_HEADER_SIZE = 40


def path_hash(path):
    value = 0
    for ch in path.lower().replace("\\", "/"):
        value = (value * HASH_PRIME + ord(ch)) & HASH_MASK
    return value


def read_rsa_keys(exe_path):
    """Returns [(n, e)] for every RSA public key text embedded in the exe."""
    text = exe_path.read_bytes().decode("utf-16-le", "ignore")
    keys = []
    for block in re.findall(r"-----BEGIN RSA PUBLIC KEY-----[\sA-Za-z0-9+/=]+?-----END RSA PUBLIC KEY-----", text):
        pem = block.replace("\r", "")
        numbers = load_pem_public_key(pem.encode()).public_numbers()
        keys.append((numbers.n, numbers.e))
    return keys


def rsa_block(block, key):
    n, e = key
    size = (n.bit_length() + 7) // 8
    return pow(int.from_bytes(block, "big"), e, n).to_bytes(size, "big")[1:]


def decrypt_bhd(data, keys):
    """Finds the key that turns this header into a BHD5 and decrypts all of it."""
    for key in keys:
        size = (key[0].bit_length() + 7) // 8
        if len(data) % size == 0 and rsa_block(data[:size], key)[:4] == b"BHD5":
            return b"".join(rsa_block(data[i:i + size], key) for i in range(0, len(data), size))
    raise ValueError("No key opens this archive header")


class Archive:
    def __init__(self, bhd_path, keys):
        self.bdt_path = bhd_path.with_suffix(".bdt")
        self.bhd = decrypt_bhd(bhd_path.read_bytes(), keys)
        self.bucket_count, self.buckets_offset = struct.unpack_from("<ii", self.bhd, 0x10)

    def find(self, path):
        """Returns the file header offset for a path, or None."""
        wanted = path_hash(path)
        count, offset = struct.unpack_from("<ii", self.bhd, self.buckets_offset + 8 * (wanted % self.bucket_count))
        for i in range(count):
            pos = offset + i * FILE_HEADER_SIZE
            if struct.unpack_from("<Q", self.bhd, pos)[0] == wanted:
                return pos
        return None

    def read(self, path):
        pos = self.find(path)
        if pos is None:
            return None

        padded, unpadded, offset, _, aes_offset = struct.unpack_from("<iiqqq", self.bhd, pos + 8)
        with open(self.bdt_path, "rb") as f:
            f.seek(offset)
            data = bytearray(f.read(padded))

        if aes_offset:
            key = self.bhd[aes_offset:aes_offset + 16]
            count = struct.unpack_from("<i", self.bhd, aes_offset + 16)[0]
            cipher = Cipher(algorithms.AES(key), modes.ECB())
            for i in range(count):
                start, end = struct.unpack_from("<qq", self.bhd, aes_offset + 20 + i * 16)
                if start != -1 and end > start:
                    dec = cipher.decryptor()
                    data[start:end] = dec.update(bytes(data[start:end])) + dec.finalize()
        return bytes(data[:unpadded]) if unpadded else bytes(data)


def open_archives(game_dir, randomizer_exe=None):
    game_dir = Path(game_dir)
    keys = read_rsa_keys(Path(randomizer_exe) if randomizer_exe else game_dir / "randomizer" / "EldenRingRandomizer.exe")

    # The exe does not carry a key for every archive, and the map art does not need them all.
    archives = {}
    for name in ARCHIVE_NAMES:
        try:
            archives[name] = Archive(game_dir / f"{name}.bhd", keys)
        except ValueError:
            pass
    return archives, keys
