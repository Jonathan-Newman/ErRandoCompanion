//! A bare DirectX 12 window for testing the overlay without the game: it clears to a dark colour every
//! frame and loads the mod DLL, which should hook it the same way it hooks Elden Ring.
//!
//! cargo run --release --example dx12_host -- <path to ErRandoCompanionMod.dll> [seconds]

use std::mem::ManuallyDrop;
use std::time::{Duration, Instant};

use windows::core::{w, Interface, HSTRING};
use windows::Win32::Foundation::*;
use windows::Win32::Graphics::Direct3D::D3D_FEATURE_LEVEL_11_0;
use windows::Win32::Graphics::Direct3D12::*;
use windows::Win32::Graphics::Dxgi::Common::*;
use windows::Win32::Graphics::Dxgi::*;
use windows::Win32::System::LibraryLoader::{GetModuleHandleW, GetProcAddress, LoadLibraryW};
use windows::Win32::System::Threading::{CreateEventW, WaitForSingleObject, INFINITE};
use windows::Win32::UI::WindowsAndMessaging::*;

const FRAMES: u32 = 2;
const WIDTH: u32 = 1280;
const HEIGHT: u32 = 720;

unsafe extern "system" fn window_proc(window: HWND, message: u32, w: WPARAM, l: LPARAM) -> LRESULT {
    if message == WM_DESTROY {
        unsafe { PostQuitMessage(0) };
        return LRESULT(0);
    }
    unsafe { DefWindowProcW(window, message, w, l) }
}

fn transition(resource: &ID3D12Resource, before: D3D12_RESOURCE_STATES, after: D3D12_RESOURCE_STATES) -> D3D12_RESOURCE_BARRIER {
    D3D12_RESOURCE_BARRIER {
        Type: D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
        Flags: D3D12_RESOURCE_BARRIER_FLAG_NONE,
        Anonymous: D3D12_RESOURCE_BARRIER_0 {
            Transition: ManuallyDrop::new(D3D12_RESOURCE_TRANSITION_BARRIER {
                pResource: ManuallyDrop::new(Some(resource.clone())),
                Subresource: D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,
                StateBefore: before,
                StateAfter: after,
            }),
        },
    }
}

/// Copies a presented frame to the CPU and writes it as a 32-bit BMP.
#[allow(clippy::too_many_arguments)]
unsafe fn capture(
    device: &ID3D12Device,
    queue: &ID3D12CommandQueue,
    allocator: &ID3D12CommandAllocator,
    list: &ID3D12GraphicsCommandList,
    fence: &ID3D12Fence,
    fence_value: &mut u64,
    event: HANDLE,
    frame: &ID3D12Resource,
    path: &str,
) -> windows::core::Result<()> {
    unsafe {
        let desc = frame.GetDesc();
        let mut footprint = D3D12_PLACED_SUBRESOURCE_FOOTPRINT::default();
        let mut total = 0u64;
        device.GetCopyableFootprints(&desc, 0, 1, 0, Some(&mut footprint), None, None, Some(&mut total));
        let mut readback: Option<ID3D12Resource> = None;
        device.CreateCommittedResource(
            &D3D12_HEAP_PROPERTIES { Type: D3D12_HEAP_TYPE_READBACK, ..Default::default() },
            D3D12_HEAP_FLAG_NONE,
            &D3D12_RESOURCE_DESC {
                Dimension: D3D12_RESOURCE_DIMENSION_BUFFER,
                Width: total,
                Height: 1,
                DepthOrArraySize: 1,
                MipLevels: 1,
                SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
                Layout: D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
                ..Default::default()
            },
            D3D12_RESOURCE_STATE_COPY_DEST,
            None,
            &mut readback,
        )?;
        let readback = readback.unwrap();

        allocator.Reset()?;
        list.Reset(allocator, None)?;
        list.ResourceBarrier(&[transition(frame, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_COPY_SOURCE)]);
        list.CopyTextureRegion(
            &D3D12_TEXTURE_COPY_LOCATION {
                pResource: ManuallyDrop::new(Some(readback.clone())),
                Type: D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
                Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 { PlacedFootprint: footprint },
            },
            0,
            0,
            0,
            &D3D12_TEXTURE_COPY_LOCATION {
                pResource: ManuallyDrop::new(Some(frame.clone())),
                Type: D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
                Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 { SubresourceIndex: 0 },
            },
            None,
        );
        list.ResourceBarrier(&[transition(frame, D3D12_RESOURCE_STATE_COPY_SOURCE, D3D12_RESOURCE_STATE_PRESENT)]);
        list.Close()?;
        queue.ExecuteCommandLists(&[Some(list.cast()?)]);
        *fence_value += 1;
        queue.Signal(fence, *fence_value)?;
        fence.SetEventOnCompletion(*fence_value, event)?;
        WaitForSingleObject(event, INFINITE);

        let mut data = std::ptr::null_mut();
        readback.Map(0, None, Some(&mut data))?;
        let (width, height, pitch) = (desc.Width as usize, desc.Height as usize, footprint.Footprint.RowPitch as usize);
        let bytes = std::slice::from_raw_parts(data as *const u8, pitch * height);
        let mut bmp = Vec::with_capacity(54 + width * height * 4);
        let image = (width * height * 4) as u32;
        bmp.extend_from_slice(b"BM");
        for v in [54 + image, 0, 54, 40] {
            bmp.extend_from_slice(&v.to_le_bytes());
        }
        bmp.extend_from_slice(&(width as i32).to_le_bytes());
        bmp.extend_from_slice(&(-(height as i32)).to_le_bytes());
        bmp.extend_from_slice(&1u16.to_le_bytes());
        bmp.extend_from_slice(&32u16.to_le_bytes());
        for v in [0u32, image, 2835, 2835, 0, 0] {
            bmp.extend_from_slice(&v.to_le_bytes());
        }
        for row in 0..height {
            for px in bytes[row * pitch..row * pitch + width * 4].chunks_exact(4) {
                bmp.extend_from_slice(&[px[2], px[1], px[0], 255]);
            }
        }
        readback.Unmap(0, None);
        std::fs::write(path, bmp).map_err(|e| windows::core::Error::new(E_FAIL, e.to_string()))?;
    }
    Ok(())
}

fn main() -> windows::core::Result<()> {
    let args: Vec<String> = std::env::args().collect();
    let dll = args.get(1).expect("pass the path of ErRandoCompanionMod.dll");
    let seconds: u64 = args.get(2).and_then(|s| s.parse().ok()).unwrap_or(20);

    unsafe {
        let instance = GetModuleHandleW(None)?;
        let class = WNDCLASSW {
            lpfnWndProc: Some(window_proc),
            hInstance: instance.into(),
            lpszClassName: w!("ErModDx12Host"),
            hCursor: LoadCursorW(None, IDC_ARROW)?,
            ..Default::default()
        };
        RegisterClassW(&class);
        let window = CreateWindowExW(
            WINDOW_EX_STYLE::default(),
            w!("ErModDx12Host"),
            w!("Overlay test host"),
            WS_OVERLAPPEDWINDOW | WS_VISIBLE,
            40,
            40,
            WIDTH as i32,
            HEIGHT as i32,
            None,
            None,
            Some(instance.into()),
            None,
        )?;

        let factory: IDXGIFactory4 = CreateDXGIFactory2(DXGI_CREATE_FACTORY_FLAGS(0))?;
        let mut device: Option<ID3D12Device> = None;
        D3D12CreateDevice(None, D3D_FEATURE_LEVEL_11_0, &mut device)?;
        let device = device.unwrap();
        let queue: ID3D12CommandQueue = device.CreateCommandQueue(&D3D12_COMMAND_QUEUE_DESC {
            Type: D3D12_COMMAND_LIST_TYPE_DIRECT,
            ..Default::default()
        })?;
        let swap_chain: IDXGISwapChain3 = factory
            .CreateSwapChainForHwnd(
                &queue,
                window,
                &DXGI_SWAP_CHAIN_DESC1 {
                    Width: 0,
                    Height: 0,
                    Format: DXGI_FORMAT_R8G8B8A8_UNORM,
                    SampleDesc: DXGI_SAMPLE_DESC { Count: 1, Quality: 0 },
                    BufferUsage: DXGI_USAGE_RENDER_TARGET_OUTPUT,
                    BufferCount: FRAMES,
                    SwapEffect: DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL,
                    ..Default::default()
                },
                None,
                None,
            )?
            .cast()?;

        let heap: ID3D12DescriptorHeap = device.CreateDescriptorHeap(&D3D12_DESCRIPTOR_HEAP_DESC {
            Type: D3D12_DESCRIPTOR_HEAP_TYPE_RTV,
            NumDescriptors: FRAMES,
            ..Default::default()
        })?;
        let step = device.GetDescriptorHandleIncrementSize(D3D12_DESCRIPTOR_HEAP_TYPE_RTV) as usize;
        let start = heap.GetCPUDescriptorHandleForHeapStart();
        let mut targets = Vec::new();
        for i in 0..FRAMES {
            let buffer: ID3D12Resource = swap_chain.GetBuffer(i)?;
            let handle = D3D12_CPU_DESCRIPTOR_HANDLE { ptr: start.ptr + i as usize * step };
            device.CreateRenderTargetView(&buffer, None, handle);
            targets.push((buffer, handle));
        }

        let allocator: ID3D12CommandAllocator = device.CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT)?;
        let list: ID3D12GraphicsCommandList = device.CreateCommandList(0, D3D12_COMMAND_LIST_TYPE_DIRECT, &allocator, None)?;
        list.Close()?;
        let fence: ID3D12Fence = device.CreateFence(0, D3D12_FENCE_FLAG_NONE)?;
        let event = CreateEventW(None, false, false, None)?;
        let mut fence_value = 0u64;

        // Load the mod the way Mod Engine does: while the program is already running.
        LoadLibraryW(&HSTRING::from(dll.as_str()))?;
        println!("loaded {dll}");

        // Read the controller every frame as the game does, so the mod's XInput hook runs here too.
        type GetState = unsafe extern "system" fn(u32, *mut [u8; 16]) -> u32;
        let xinput = LoadLibraryW(w!("xinput1_4.dll"))?;
        let get_state: GetState = std::mem::transmute(GetProcAddress(xinput, windows::core::s!("XInputGetState")).unwrap());
        let mut pad_reads = [0u32; 2];

        let capture_path = std::env::var("ER_HOST_CAPTURE").ok();
        let mut captured = false;
        let started = Instant::now();
        let mut message = MSG::default();
        while started.elapsed() < Duration::from_secs(seconds) {
            // Flip-sequential keeps a presented frame intact, so the last one (overlay included) can be read back.
            if let Some(path) = capture_path.as_deref().filter(|_| !captured && started.elapsed() > Duration::from_secs(seconds * 2 / 3)) {
                let previous = (swap_chain.GetCurrentBackBufferIndex() + FRAMES - 1) % FRAMES;
                capture(&device, &queue, &allocator, &list, &fence, &mut fence_value, event, &targets[previous as usize].0, path)?;
                println!("captured {path}; controller reads (not connected, connected): {pad_reads:?}");
                captured = true;
            }
            while PeekMessageW(&mut message, None, 0, 0, PM_REMOVE).as_bool() {
                if message.message == WM_QUIT {
                    return Ok(());
                }
                let _ = TranslateMessage(&message);
                DispatchMessageW(&message);
            }

            let mut state = [0u8; 16];
            pad_reads[(get_state(0, &mut state) == 0) as usize] += 1;

            let (buffer, handle) = &targets[swap_chain.GetCurrentBackBufferIndex() as usize];
            allocator.Reset()?;
            list.Reset(&allocator, None)?;
            list.ResourceBarrier(&[transition(buffer, D3D12_RESOURCE_STATE_PRESENT, D3D12_RESOURCE_STATE_RENDER_TARGET)]);
            list.ClearRenderTargetView(*handle, &[0.12, 0.14, 0.18, 1.0], None);
            list.ResourceBarrier(&[transition(buffer, D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12_RESOURCE_STATE_PRESENT)]);
            list.Close()?;
            queue.ExecuteCommandLists(&[Some(list.cast()?)]);
            swap_chain.Present(1, DXGI_PRESENT(0)).ok()?;

            fence_value += 1;
            queue.Signal(&fence, fence_value)?;
            if fence.GetCompletedValue() < fence_value {
                fence.SetEventOnCompletion(fence_value, event)?;
                WaitForSingleObject(event, INFINITE);
            }
        }
    }
    Ok(())
}
