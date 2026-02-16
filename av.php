<?php

//EXAMPLE av profile image 

/*
 * Copyright (c) Fiona Sweet <fiona@pobox.holoneon.com>
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */


function valid_uuidv4(string $uuid): bool
{
    return preg_match(
        '/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/i',
        $uuid
    ) === 1;
}

$cacheDir = __DIR__ . "/avpic-cache";

if (empty($_GET['uuid'])) exit('no uuid');

$uuid = $_GET['uuid'] ?? '';
$uuid = str_replace('.png','',$uuid);

if (!valid_uuidv4($uuid)) exit('invalid uuid');

include('pdo-only.php');
$sql = "SELECT profileImage FROM userprofile WHERE useruuid = ?";
$stmt = $pdo->prepare($sql);
$stmt->execute([$uuid]);
$row = $stmt->fetch(PDO::FETCH_ASSOC);

if (!$row || empty($row['profileImage'])) exit('no av match');

$png = $cacheDir . '/' . $uuid . '.png';

if (file_exists($png))
{
    $img = file_get_contents($png);
} else {
    $d = file_get_contents('http://10.99.0.1:8003/assets/'.$row['profileImage']);
    if (($d!='')&&(strstr($d,'<Data>')))
    {
        $r = explode('</Data>',$d);
        $d = array_shift($r);
        $r = explode('<Data>',$d);
        $d = array_pop($r);

        $img = base64_decode($d);
        $fp = fopen($cacheDir.'/'.$uuid.'.j2k','w');
        fwrite($fp,$img);
        fclose($fp);

        $src = escapeshellarg($cacheDir.'/'.$uuid.'.j2k');
        $dst = escapeshellarg($png);
        system("/usr/bin/convert $src -strip -size 256x256 -scale 256x256 -density 120 $dst");

        $img = file_get_contents($png);
    } else {
        exit('missing asset '.$row['profileImage']);
    }
}

if ($img!='')
{

    //eTag support
    $etag = '"' . sha1_file($png) . '"';
    Header("ETag: $etag");

    if (isset($_SERVER['HTTP_IF_NONE_MATCH'])) {
        if (trim($_SERVER['HTTP_IF_NONE_MATCH']) === $etag) {
            Header("HTTP/1.1 304 Not Modified");
            exit();
        }
    }

    Header("Last-Modified: " . gmdate("D, d M Y H:i:s", filemtime($png)) . " GMT");

    Header("Content-type: image/png");
    Header("Content-length: ".strlen($img));
    Header("Cache-Control: public, max-age=86400");
    echo $img;
}

